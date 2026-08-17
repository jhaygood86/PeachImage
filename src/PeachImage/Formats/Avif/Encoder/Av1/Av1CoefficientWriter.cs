using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Writes one square DCT_DCT transform block's quantized coefficients -- the write-side mirror of
/// <see cref="Av1TileDecoder"/>'s private <c>Coeffs()</c> (spec §5.11.39), restricted to this encoder's v1
/// scope: always <c>Av1TxType.DctDct</c> (so <see cref="Av1TxClass"/> is always <c>Class2D</c>, letting the
/// context derivations below skip the horizontal-/vertical-only branches <c>Coeffs()</c> otherwise needs),
/// square tx sizes only, and <c>tx_mode = TX_MODE_LARGEST</c> (so the transform block always exactly equals
/// the coding block -- <c>Coeffs()</c>'s luma <c>all_zero</c> context reduces to a constant 0 under that
/// condition; see <see cref="WriteCoeffs"/>).
///
/// <para>Unlike the decoder, this encoder already knows every coefficient in the block before writing any
/// of them (the whole block was quantized upfront by <see cref="Av1ForwardQuantizer"/>), so the "neighbor
/// magnitude" context lookups <c>Coeffs()</c> serves from its progressively-built <c>_quant</c> array are
/// served here from the same, already-complete <c>quantLevels</c> array -- equivalent by construction,
/// since AV1's scan order guarantees every context-neighbor position precedes the position being coded.</para>
/// </summary>
internal static class Av1CoefficientWriter
{
    /// <summary>Per-plane above/left coefficient-level and DC-sign context state, indexed in 4x4 ("mode info") units across the whole plane -- the write-side analog of <see cref="Av1TileDecoder"/>'s <c>_aboveLevelContext</c>/<c>_aboveDcContext</c>/<c>_leftLevelContext</c>/<c>_leftDcContext</c>.</summary>
    public sealed class PlaneContext(int width4, int height4)
    {
        public int[] AboveLevel { get; } = new int[width4];

        public int[] AboveDc { get; } = new int[width4];

        public int[] LeftLevel { get; } = new int[height4];

        public int[] LeftDc { get; } = new int[height4];

        public int MaxX4 { get; } = width4;

        public int MaxY4 { get; } = height4;
    }

    /// <summary>
    /// Writes <paramref name="quantLevels"/> (signed, flat <paramref name="size"/> x <paramref name="size"/>
    /// row-major) at 4x4-unit position <c>(x4, y4)</c> in plane <paramref name="ptype"/> (0 = luma, 1 =
    /// chroma), updating <paramref name="planeCtx"/>'s above/left context state. Returns the eob written
    /// (0 if the block was signalled all-zero).
    ///
    /// <para><paramref name="writeLumaTxType"/> (luma callers only): invoked to write the <c>tx_type</c>
    /// symbol, but only when the block turns out <em>not</em> all-zero -- <c>Coeffs()</c> only reads
    /// <c>transform_type()</c> after establishing <c>all_zero == 0</c>, so writing it unconditionally
    /// (before this method even determines <c>eob</c>) would emit an extra symbol on every all-zero block a
    /// real decoder never expects, desyncing the entire rest of the tile. Ignored for chroma, whose tx type
    /// is always derived from <c>uv_mode</c> rather than signalled (see <c>ComputeTxType</c>).</para>
    /// </summary>
    public static int WriteCoeffs(Av1SymbolEncoder s, Av1CdfContext cdf, int[] quantLevels, int size, int ptype, int x4, int y4, PlaneContext planeCtx, Action? writeLumaTxType = null)
    {
        int txSz = Av1ForwardTransform.SizeToTxSz(size);
        int txSzCtx = (Av1CoeffTables.TxSizeSqr[txSz] + Av1CoeffTables.TxSizeSqrUp[txSz] + 1) >> 1;
        int w4 = size >> 2;
        int h4 = size >> 2;

        int[] scan = Av1ScanTables.GetScan(txSz, Av1TxType.DctDct);

        int eob = 0;
        for (int c = 0; c < size * size; c++)
        {
            if (quantLevels[scan[c]] != 0)
            {
                eob = c + 1;
            }
        }

        // ptype == 0 (luma): tx_mode == TX_MODE_LARGEST means the transform block always exactly equals
        // the coding block, so Coeffs()'s "bw == w && bh == h" check is always true here -> context 0.
        // ptype == 1 (chroma): the OR-accumulated above/left context always applies (Coeffs() never takes
        // the luma-only size-match shortcut for chroma).
        int allZeroCtx = ptype == 0 ? 0 : GetChromaAllZeroContext(x4, y4, w4, h4, planeCtx);
        bool allZero = eob == 0;
        s.WriteSymbol(cdf.TxbSkip[txSzCtx][allZeroCtx], allZero ? 1 : 0);

        int culLevel = 0;
        int dcCategory = 0;

        if (!allZero)
        {
            writeLumaTxType?.Invoke();
            WriteEobPt(s, cdf, txSz, txSzCtx, ptype, eob);

            for (int c = eob - 1; c >= 0; c--)
            {
                int pos = scan[c];
                int absLevel = Math.Abs(quantLevels[pos]);

                // The base+br stages together can represent levels up to NumBaseLevels + 1 (the base
                // symbol's own max, read as `level` directly) + CoeffBaseRange (the br loop's max total
                // contribution) = 3 + 12 = 15; anything beyond that is left for the golomb tail below.
                int cappedLevel = Math.Min(absLevel, Av1CoeffTables.NumBaseLevels + 1 + Av1CoeffTables.CoeffBaseRange);

                if (c == eob - 1)
                {
                    int ctx = GetCoeffBaseEobCtx(txSz, c);
                    int symbol = Math.Min(cappedLevel, Av1CoeffTables.NumBaseLevels + 1) - 1;
                    s.WriteSymbol(cdf.CoeffBaseEob[txSzCtx][ptype][ctx - Av1CoeffTables.SigCoefContexts + Av1CoeffTables.SigCoefContextsEob], symbol);
                }
                else
                {
                    int ctx = GetCoeffBaseCtx(txSz, quantLevels, pos);
                    int symbol = Math.Min(cappedLevel, Av1CoeffTables.NumBaseLevels + 1);
                    s.WriteSymbol(cdf.CoeffBase[txSzCtx][ptype][ctx], symbol);
                }

                if (cappedLevel > Av1CoeffTables.NumBaseLevels)
                {
                    int brCtx = GetCoeffBrCtx(txSz, quantLevels, pos);
                    var brCdf = cdf.CoeffBr[Math.Min(txSzCtx, Av1TxSize.Tx32x32)][ptype][brCtx];

                    // The base symbol read on the decode side is always exactly NumBaseLevels + 1 (3) once
                    // it triggers the br loop at all (both CoeffBase's max symbol and CoeffBaseEob's
                    // level = symbol + 1 top out at 3), so the br loop's job is to add the remainder from
                    // that fixed starting point, not from NumBaseLevels itself.
                    int remaining = cappedLevel - (Av1CoeffTables.NumBaseLevels + 1);
                    int maxIterations = Av1CoeffTables.CoeffBaseRange / (Av1CoeffTables.BrCdfSize - 1);
                    for (int idx = 0; idx < maxIterations; idx++)
                    {
                        int step = Math.Min(remaining, Av1CoeffTables.BrCdfSize - 1);
                        s.WriteSymbol(brCdf, step);
                        remaining -= step;
                        if (step < Av1CoeffTables.BrCdfSize - 1)
                        {
                            break;
                        }
                    }
                }
            }

            for (int c = 0; c < eob; c++)
            {
                int pos = scan[c];
                int trueLevel = quantLevels[pos];
                int absLevel = Math.Abs(trueLevel);
                int sign = trueLevel < 0 ? 1 : 0;

                if (absLevel != 0)
                {
                    if (c == 0)
                    {
                        int dcSignCtx = GetDcSignContext(x4, y4, w4, h4, planeCtx);
                        s.WriteSymbol(cdf.DcSign[ptype][dcSignCtx], sign);
                    }
                    else
                    {
                        s.WriteLiteral((uint)sign, 1);
                    }
                }

                if (absLevel > Av1CoeffTables.NumBaseLevels + Av1CoeffTables.CoeffBaseRange)
                {
                    WriteGolomb(s, absLevel - Av1CoeffTables.CoeffBaseRange - Av1CoeffTables.NumBaseLevels);
                }

                if (pos == 0 && absLevel > 0)
                {
                    dcCategory = sign != 0 ? 1 : 2;
                }

                int maskedLevel = absLevel & 0xFFFFF;
                culLevel += maskedLevel;
            }

            culLevel = Math.Min(63, culLevel);
        }

        for (int i = 0; i < w4; i++)
        {
            if (x4 + i < planeCtx.MaxX4)
            {
                planeCtx.AboveLevel[x4 + i] = culLevel;
                planeCtx.AboveDc[x4 + i] = dcCategory;
            }
        }

        for (int i = 0; i < h4; i++)
        {
            if (y4 + i < planeCtx.MaxY4)
            {
                planeCtx.LeftLevel[y4 + i] = culLevel;
                planeCtx.LeftDc[y4 + i] = dcCategory;
            }
        }

        return eob;
    }

    /// <summary>Write-side of <c>Coeffs()</c>'s <c>eob_pt</c>/<c>eob_extra</c>/literal encoding: given the target <paramref name="eob"/>, determines and writes the bucket symbol plus refinement bits.</summary>
    private static void WriteEobPt(Av1SymbolEncoder s, Av1CdfContext cdf, int txSz, int txSzCtx, int ptype, int eob)
    {
        _ = txSzCtx;
        int eobMultisize = Math.Min(Av1TxDimensions.WidthLog2[txSz], 5) + Math.Min(Av1TxDimensions.HeightLog2[txSz], 5) - 4;

        int k; // eobPt
        int offset;
        if (eob == 1)
        {
            k = 1;
            offset = 0;
        }
        else if (eob == 2)
        {
            k = 2;
            offset = 0;
        }
        else
        {
            int m = eob - 1;
            int log2M = Av1CdfAdaptation.FloorLog2((uint)m);
            k = log2M + 2;
            offset = m - (1 << log2M);
        }

        int symbol = k - 1;
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
        s.WriteSymbol(eobCdf, symbol);

        int eobShift = Math.Max(-1, k - 3);
        if (eobShift >= 0)
        {
            int eobExtraBit = (offset >> eobShift) & 1;
            s.WriteSymbol(cdf.EobExtra[txSzCtx][ptype][k - 3], eobExtraBit);

            for (int i = 1; i < Math.Max(0, k - 2); i++)
            {
                int shift = Math.Max(0, k - 2) - 1 - i;
                int bit = (offset >> shift) & 1;
                s.WriteLiteral((uint)bit, 1);
            }
        }
    }

    /// <summary><c>get_coeff_base_ctx()</c>'s <c>isEob</c> branch (spec §8.3.2), Class2D-only.</summary>
    private static int GetCoeffBaseEobCtx(int txSz, int c)
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

    /// <summary><c>get_coeff_base_ctx()</c>'s non-eob branch (spec §8.3.2), Class2D-only -- <paramref name="quantLevels"/> is this encoder's own already-complete block, standing in for the decoder's progressively-built <c>_quant</c>.</summary>
    private static int GetCoeffBaseCtx(int txSz, int[] quantLevels, int pos)
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
                mag += Math.Min(Math.Abs(quantLevels[(refRow << bwl) + refCol]), 3);
            }
        }

        int ctx = Math.Min((mag + 1) >> 1, 4);

        if (row == 0 && col == 0)
        {
            return 0;
        }

        return ctx + Av1CoeffTables.CoeffBaseCtxOffset[txSz][Math.Min(row, 4)][Math.Min(col, 4)];
    }

    /// <summary><c>get_coeff_br_ctx()</c> (spec §8.3.2), Class2D-only.</summary>
    private static int GetCoeffBrCtx(int txSz, int[] quantLevels, int pos)
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
                mag += Math.Min(Math.Abs(quantLevels[(refRow * txw) + refCol]), Av1CoeffTables.CoeffBaseRange + Av1CoeffTables.NumBaseLevels + 1);
            }
        }

        mag = Math.Min((mag + 1) >> 1, 6);

        if (pos == 0)
        {
            return mag;
        }

        return row < 2 && col < 2 ? mag + 7 : mag + 14;
    }

    /// <summary><c>dc_sign</c>'s CDF context derivation (spec §8.3.2).</summary>
    private static int GetDcSignContext(int x4, int y4, int w4, int h4, PlaneContext ctx)
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

    /// <summary><c>all_zero</c>'s CDF context derivation for chroma (spec §8.3.2) -- luma always uses context 0 under this encoder's fixed TX_MODE_LARGEST configuration (see <see cref="WriteCoeffs"/>'s remarks).</summary>
    private static int GetChromaAllZeroContext(int x4, int y4, int w4, int h4, PlaneContext ctx)
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

        // bw*bh == w*h always here too (chroma tx == chroma coding-block size under TX_MODE_LARGEST), so
        // Coeffs()'s "+= 3 when bw*bh > w*h" never applies.
        return (above != 0 ? 1 : 0) + (leftAcc != 0 ? 1 : 0) + 7;
    }

    /// <summary>Write-side of <c>Coeffs()</c>'s Exp-Golomb tail (spec §5.11.39) for levels beyond <c>NumBaseLevels + CoeffBaseRange</c>.</summary>
    private static void WriteGolomb(Av1SymbolEncoder s, int value)
    {
        // Decode: reads `length` "continue" bits (0 = continue, 1 = stop) via ReadLiteral(1), then
        // `length - 1` data bits (MSB first, excluding the implicit leading 1), reconstructing
        // x = 1 followed by those data bits. So x's bit length is `length`, and `length - 1` data bits
        // (all but the leading 1) are written after `length - 1` zero "continue" bits and a final one bit.
        int x = value; // decode computes x directly as the golomb value (>= 1)
        int length = Av1CdfAdaptation.FloorLog2((uint)x) + 1;

        for (int i = 0; i < length - 1; i++)
        {
            s.WriteLiteral(0, 1);
        }

        s.WriteLiteral(1, 1);

        for (int i = length - 2; i >= 0; i--)
        {
            int bit = (x >> i) & 1;
            s.WriteLiteral((uint)bit, 1);
        }
    }
}
