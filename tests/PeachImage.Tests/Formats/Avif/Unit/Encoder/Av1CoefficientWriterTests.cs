using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1CoefficientWriter"/> via a minimal reader ported independently from
/// <c>Av1TileDecoder.Coeffs()</c>'s source (not from the writer itself), driven by the real
/// <see cref="Av1SymbolDecoder"/>. This is a self-consistency gate on top of <see cref="Av1SymbolEncoderTests"/>'s
/// entropy-layer verification -- the definitive gate (decoding through the real, unmodified
/// <see cref="Av1TileDecoder"/>) lands once the full tile/frame encoder exists and can produce a real
/// decodable AVIF file.
/// </summary>
public class Av1CoefficientWriterTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void WriteCoeffs_AllZeroBlock_RoundTrips(int size)
    {
        int[] quant = new int[size * size];
        AssertRoundTrips(quant, size, ptype: 0);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void WriteCoeffs_DcOnly_RoundTrips(int size)
    {
        int[] quant = new int[size * size];
        quant[0] = 5;
        AssertRoundTrips(quant, size, ptype: 0);

        int[] quantNeg = new int[size * size];
        quantNeg[0] = -5;
        AssertRoundTrips(quantNeg, size, ptype: 0);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void WriteCoeffs_SeveralLowFrequencyCoefficients_RoundTrips(int size)
    {
        int[] quant = new int[size * size];
        quant[0] = 12;
        quant[1] = -3;
        quant[size] = 2;
        quant[size + 1] = -1;
        AssertRoundTrips(quant, size, ptype: 0);
        AssertRoundTrips(quant, size, ptype: 1);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void WriteCoeffs_LargeMagnitudeRequiringGolomb_RoundTrips(int size)
    {
        int[] quant = new int[size * size];
        quant[0] = 500; // well beyond NumBaseLevels + CoeffBaseRange (14), forces the golomb tail
        AssertRoundTrips(quant, size, ptype: 0);

        int[] quantNeg = new int[size * size];
        quantNeg[0] = -500;
        AssertRoundTrips(quantNeg, size, ptype: 0);
    }

    [Fact]
    public void WriteCoeffs_HighFrequencyOnly_RoundTrips()
    {
        const int size = 8;
        int[] quant = new int[size * size];
        quant[(size * size) - 1] = 3;
        quant[(size * size) - 2] = -1;
        AssertRoundTrips(quant, size, ptype: 0);
    }

    [Fact]
    public void WriteCoeffs_ScatteredRandomPattern_RoundTrips()
    {
        const int size = 16;
        var random = new Random(555);
        int[] quant = new int[size * size];
        for (int i = 0; i < 20; i++)
        {
            int pos = random.Next(quant.Length);
            quant[pos] = random.Next(-30, 31);
        }

        AssertRoundTrips(quant, size, ptype: 0);
        AssertRoundTrips(quant, size, ptype: 1);
    }

    [Fact]
    public void WriteCoeffs_MultipleBlocksInSequence_ContextCarriesCorrectly()
    {
        const int size = 8;
        const int planeWidth4 = 64;
        const int planeHeight4 = 64;

        var random = new Random(31337);
        var blocks = new List<int[]>();
        for (int i = 0; i < 6; i++)
        {
            int[] quant = new int[size * size];
            int nonZeroCount = random.Next(0, 6);
            for (int j = 0; j < nonZeroCount; j++)
            {
                quant[random.Next(quant.Length)] = random.Next(-20, 21);
            }

            blocks.Add(quant);
        }

        var cdf = new Av1CdfContext(baseQIdx: 64);
        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);
        var encodeCtx = new Av1CoefficientWriter.PlaneContext(planeWidth4, planeHeight4);

        int w4 = size / 4;
        for (int i = 0; i < blocks.Count; i++)
        {
            int x4 = (i * w4) % planeWidth4;
            int y4 = ((i * w4) / planeWidth4) * w4;
            Av1CoefficientWriter.WriteCoeffs(encoder, cdf, blocks[i], size, ptype: 0, x4, y4, encodeCtx);
        }

        byte[] data = encoder.Flush();
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);
        var decodeCdf = new Av1CdfContext(baseQIdx: 64);
        var decodeCtx = new MiniPlaneContext(planeWidth4, planeHeight4);

        for (int i = 0; i < blocks.Count; i++)
        {
            int x4 = (i * w4) % planeWidth4;
            int y4 = ((i * w4) / planeWidth4) * w4;
            int[] decoded = MiniCoeffReader.ReadCoeffs(decoder, decodeCdf, size, ptype: 0, x4, y4, decodeCtx);
            Assert.Equal(blocks[i], decoded);
        }
    }

    private static void AssertRoundTrips(int[] quant, int size, int ptype)
    {
        const int planeWidth4 = 64;
        const int planeHeight4 = 64;

        var encodeCdf = new Av1CdfContext(baseQIdx: 64);
        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);
        var encodeCtx = new Av1CoefficientWriter.PlaneContext(planeWidth4, planeHeight4);
        Av1CoefficientWriter.WriteCoeffs(encoder, encodeCdf, quant, size, ptype, x4: 0, y4: 0, encodeCtx);

        byte[] data = encoder.Flush();
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);
        var decodeCdf = new Av1CdfContext(baseQIdx: 64);
        var decodeCtx = new MiniPlaneContext(planeWidth4, planeHeight4);

        int[] decoded = MiniCoeffReader.ReadCoeffs(decoder, decodeCdf, size, ptype, x4: 0, y4: 0, decodeCtx);

        Assert.Equal(quant, decoded);
    }
}

/// <summary>Above/left coefficient-context state for <see cref="MiniCoeffReader"/>, independent of <see cref="Av1CoefficientWriter.PlaneContext"/>.</summary>
internal sealed class MiniPlaneContext(int width4, int height4)
{
    public int[] AboveLevel { get; } = new int[width4];

    public int[] AboveDc { get; } = new int[width4];

    public int[] LeftLevel { get; } = new int[height4];

    public int[] LeftDc { get; } = new int[height4];

    public int MaxX4 { get; } = width4;

    public int MaxY4 { get; } = height4;
}

/// <summary>
/// A minimal transform-block coefficient reader, ported directly from <c>Av1TileDecoder.Coeffs()</c>'s
/// source (independently of <see cref="Av1CoefficientWriter"/>, not by calling it) for square DCT_DCT
/// blocks -- exactly the shape <see cref="Av1CoefficientWriter"/> produces. Test-only.
/// </summary>
internal static class MiniCoeffReader
{
    public static int[] ReadCoeffs(Av1SymbolDecoder s, Av1CdfContext cdf, int size, int ptype, int x4, int y4, MiniPlaneContext ctx)
    {
        int txSz = Av1ForwardTransform.SizeToTxSz(size);
        int txSzCtx = (Av1CoeffTables.TxSizeSqr[txSz] + Av1CoeffTables.TxSizeSqrUp[txSz] + 1) >> 1;
        int w4 = size >> 2;
        int h4 = size >> 2;
        var quant = new int[size * size];

        int allZeroCtx = ptype == 0 ? 0 : ChromaAllZeroContext(x4, y4, w4, h4, ctx);
        bool allZero = s.ReadSymbol(cdf.TxbSkip[txSzCtx][allZeroCtx]) != 0;

        int culLevel = 0;
        int dcCategory = 0;
        int eob = 0;

        if (!allZero)
        {
            int[] scan = Av1ScanTables.GetScan(txSz, Av1TxType.DctDct);
            int eobMultisize = Math.Min(Av1TxDimensions.WidthLog2[txSz], 5) + Math.Min(Av1TxDimensions.HeightLog2[txSz], 5) - 4;
            var eobCdf = eobMultisize switch
            {
                0 => cdf.EobPt16[ptype][0],
                1 => cdf.EobPt32[ptype][0],
                2 => cdf.EobPt64[ptype][0],
                3 => cdf.EobPt128[ptype][0],
                4 => cdf.EobPt256[ptype][0],
                5 => cdf.EobPt512[ptype],
                _ => cdf.EobPt1024[ptype],
            };
            int eobPt = s.ReadSymbol(eobCdf) + 1;
            eob = eobPt < 2 ? eobPt : (1 << (eobPt - 2)) + 1;
            int eobShift = Math.Max(-1, eobPt - 3);
            if (eobShift >= 0)
            {
                bool eobExtra = s.ReadSymbol(cdf.EobExtra[txSzCtx][ptype][eobPt - 3]) != 0;
                if (eobExtra)
                {
                    eob += 1 << eobShift;
                }

                for (int i = 1; i < Math.Max(0, eobPt - 2); i++)
                {
                    eobShift = Math.Max(0, eobPt - 2) - 1 - i;
                    if (s.ReadLiteral(1) != 0)
                    {
                        eob += 1 << eobShift;
                    }
                }
            }

            for (int c = eob - 1; c >= 0; c--)
            {
                int pos = scan[c];
                int level;
                if (c == eob - 1)
                {
                    int ctxIdx = CoeffBaseEobCtx(txSz, c);
                    level = s.ReadSymbol(cdf.CoeffBaseEob[txSzCtx][ptype][ctxIdx - Av1CoeffTables.SigCoefContexts + Av1CoeffTables.SigCoefContextsEob]) + 1;
                }
                else
                {
                    int ctxIdx = CoeffBaseCtx(txSz, quant, pos);
                    level = s.ReadSymbol(cdf.CoeffBase[txSzCtx][ptype][ctxIdx]);
                }

                if (level > Av1CoeffTables.NumBaseLevels)
                {
                    int brCtx = CoeffBrCtx(txSz, quant, pos);
                    var brCdf = cdf.CoeffBr[Math.Min(txSzCtx, Av1TxSize.Tx32x32)][ptype][brCtx];
                    for (int idx = 0; idx < Av1CoeffTables.CoeffBaseRange / (Av1CoeffTables.BrCdfSize - 1); idx++)
                    {
                        int coeffBr = s.ReadSymbol(brCdf);
                        level += coeffBr;
                        if (coeffBr < Av1CoeffTables.BrCdfSize - 1)
                        {
                            break;
                        }
                    }
                }

                quant[pos] = level;
            }

            for (int c = 0; c < eob; c++)
            {
                int pos = scan[c];
                int sign;
                if (quant[pos] != 0)
                {
                    if (c == 0)
                    {
                        int dcSignCtx = DcSignContext(x4, y4, w4, h4, ctx);
                        sign = s.ReadSymbol(cdf.DcSign[ptype][dcSignCtx]);
                    }
                    else
                    {
                        sign = (int)s.ReadLiteral(1);
                    }
                }
                else
                {
                    sign = 0;
                }

                if (quant[pos] > Av1CoeffTables.NumBaseLevels + Av1CoeffTables.CoeffBaseRange)
                {
                    int length = 0;
                    bool golombLengthBit;
                    do
                    {
                        length++;
                        golombLengthBit = s.ReadLiteral(1) != 0;
                    }
                    while (!golombLengthBit);

                    int x = 1;
                    for (int i = length - 2; i >= 0; i--)
                    {
                        x = (x << 1) | (int)s.ReadLiteral(1);
                    }

                    quant[pos] = x + Av1CoeffTables.CoeffBaseRange + Av1CoeffTables.NumBaseLevels;
                }

                if (pos == 0 && quant[pos] > 0)
                {
                    dcCategory = sign != 0 ? 1 : 2;
                }

                quant[pos] &= 0xFFFFF;
                culLevel += quant[pos];
                if (sign != 0)
                {
                    quant[pos] = -quant[pos];
                }
            }

            culLevel = Math.Min(63, culLevel);
        }

        for (int i = 0; i < w4; i++)
        {
            if (x4 + i < ctx.MaxX4)
            {
                ctx.AboveLevel[x4 + i] = culLevel;
                ctx.AboveDc[x4 + i] = dcCategory;
            }
        }

        for (int i = 0; i < h4; i++)
        {
            if (y4 + i < ctx.MaxY4)
            {
                ctx.LeftLevel[y4 + i] = culLevel;
                ctx.LeftDc[y4 + i] = dcCategory;
            }
        }

        return quant;
    }

    private static int ChromaAllZeroContext(int x4, int y4, int w4, int h4, MiniPlaneContext ctx)
    {
        int above = 0;
        int leftAcc = 0;
        for (int i = 0; i < w4; i++)
        {
            if (x4 + i < ctx.MaxX4)
            {
                above |= ctx.AboveLevel[x4 + i];
                above |= ctx.AboveDc[x4 + i];
            }
        }

        for (int i = 0; i < h4; i++)
        {
            if (y4 + i < ctx.MaxY4)
            {
                leftAcc |= ctx.LeftLevel[y4 + i];
                leftAcc |= ctx.LeftDc[y4 + i];
            }
        }

        return (above != 0 ? 1 : 0) + (leftAcc != 0 ? 1 : 0) + 7;
    }

    private static int DcSignContext(int x4, int y4, int w4, int h4, MiniPlaneContext ctx)
    {
        int dcSign = 0;
        for (int k = 0; k < w4; k++)
        {
            if (x4 + k < ctx.MaxX4)
            {
                int sign = ctx.AboveDc[x4 + k];
                if (sign == 1)
                {
                    dcSign--;
                }
                else if (sign == 2)
                {
                    dcSign++;
                }
            }
        }

        for (int k = 0; k < h4; k++)
        {
            if (y4 + k < ctx.MaxY4)
            {
                int sign = ctx.LeftDc[y4 + k];
                if (sign == 1)
                {
                    dcSign--;
                }
                else if (sign == 2)
                {
                    dcSign++;
                }
            }
        }

        if (dcSign < 0)
        {
            return 1;
        }

        return dcSign > 0 ? 2 : 0;
    }

    private static int CoeffBaseEobCtx(int txSz, int c)
    {
        int adjTxSz = Av1CoeffTables.AdjustedTxSize[txSz];
        int bwl = Av1TxDimensions.WidthLog2[adjTxSz];
        int height = Av1TxDimensions.Height[adjTxSz];

        if (c == 0)
        {
            return Av1CoeffTables.SigCoefContexts - 4;
        }

        if (c <= (height << bwl) / 8)
        {
            return Av1CoeffTables.SigCoefContexts - 3;
        }

        if (c <= (height << bwl) / 4)
        {
            return Av1CoeffTables.SigCoefContexts - 2;
        }

        return Av1CoeffTables.SigCoefContexts - 1;
    }

    private static int CoeffBaseCtx(int txSz, int[] quant, int pos)
    {
        int adjTxSz = Av1CoeffTables.AdjustedTxSize[txSz];
        int bwl = Av1TxDimensions.WidthLog2[adjTxSz];
        int width = 1 << bwl;
        int height = Av1TxDimensions.Height[adjTxSz];
        int row = pos >> bwl;
        int col = pos - (row << bwl);
        int mag = 0;

        for (int idx = 0; idx < Av1CoeffTables.SigRefDiffOffsetNum; idx++)
        {
            int refRow = row + Av1CoeffTables.SigRefDiffOffset[Av1TxClass.Class2D][idx][0];
            int refCol = col + Av1CoeffTables.SigRefDiffOffset[Av1TxClass.Class2D][idx][1];
            if (refRow >= 0 && refCol >= 0 && refRow < height && refCol < width)
            {
                mag += Math.Min(Math.Abs(quant[(refRow << bwl) + refCol]), 3);
            }
        }

        int ctx = Math.Min((mag + 1) >> 1, 4);

        if (row == 0 && col == 0)
        {
            return 0;
        }

        return ctx + Av1CoeffTables.CoeffBaseCtxOffset[txSz][Math.Min(row, 4)][Math.Min(col, 4)];
    }

    private static int CoeffBrCtx(int txSz, int[] quant, int pos)
    {
        int adjTxSz = Av1CoeffTables.AdjustedTxSize[txSz];
        int bwl = Av1TxDimensions.WidthLog2[adjTxSz];
        int txw = Av1TxDimensions.Width[adjTxSz];
        int txh = Av1TxDimensions.Height[adjTxSz];
        int row = pos >> bwl;
        int col = pos - (row << bwl);
        int mag = 0;

        for (int idx = 0; idx < 3; idx++)
        {
            int refRow = row + Av1CoeffTables.MagRefOffsetWithTxClass[Av1TxClass.Class2D][idx][0];
            int refCol = col + Av1CoeffTables.MagRefOffsetWithTxClass[Av1TxClass.Class2D][idx][1];
            if (refRow >= 0 && refCol >= 0 && refRow < txh && refCol < (1 << bwl))
            {
                mag += Math.Min(quant[(refRow * txw) + refCol], Av1CoeffTables.CoeffBaseRange + Av1CoeffTables.NumBaseLevels + 1);
            }
        }

        mag = Math.Min((mag + 1) >> 1, 6);

        if (pos == 0)
        {
            return mag;
        }

        return row < 2 && col < 2 ? mag + 7 : mag + 14;
    }
}
