using System.Buffers;
using PeachImage.Formats.Jpeg2000.Internal;

namespace PeachImage.Formats.Jpeg2000.Decoding;

/// <summary>A codeword segment of a code-block while packets are being read; segments of one block form a linked list in a <see cref="J2kSegmentArena"/>.</summary>
internal struct J2kSegmentState
{
    public int MaxPasses;
    public int Passes;
    public int Length;
    public int FirstChunk;
    public int LastChunk;
    public int Next;
}

/// <summary>A run of packet-body bytes belonging to a segment, as an offset into the tile data.</summary>
internal struct J2kChunk
{
    public int Offset;
    public int Length;
    public int Next;
}

/// <summary>Pooled storage for the segments and byte chunks of every code-block in a tile, so reading packets allocates nothing per block.</summary>
internal sealed class J2kSegmentArena : IDisposable
{
    private J2kSegmentState[] _segments = ArrayPool<J2kSegmentState>.Shared.Rent(256);
    private J2kChunk[] _chunks = ArrayPool<J2kChunk>.Shared.Rent(256);
    private int _segmentCount;
    private int _chunkCount;

    public ref J2kSegmentState Segment(int index) => ref _segments[index];

    public ref J2kChunk Chunk(int index) => ref _chunks[index];

    public int AddSegment(int maxPasses)
    {
        if (_segmentCount == _segments.Length)
        {
            Grow(ref _segments);
        }

        _segments[_segmentCount] = new J2kSegmentState { MaxPasses = maxPasses, FirstChunk = -1, LastChunk = -1, Next = -1 };
        return _segmentCount++;
    }

    public void AddChunk(int segment, int offset, int length)
    {
        if (_chunkCount == _chunks.Length)
        {
            Grow(ref _chunks);
        }

        int index = _chunkCount++;
        _chunks[index] = new J2kChunk { Offset = offset, Length = length, Next = -1 };
        ref var state = ref _segments[segment];
        if (state.LastChunk < 0)
        {
            state.FirstChunk = index;
        }
        else
        {
            _chunks[state.LastChunk].Next = index;
        }

        state.LastChunk = index;
        state.Length += length;
    }

    public void Dispose()
    {
        ArrayPool<J2kSegmentState>.Shared.Return(_segments);
        ArrayPool<J2kChunk>.Shared.Return(_chunks);
        _segments = [];
        _chunks = [];
    }

    private static void Grow<T>(ref T[] array)
    {
        var bigger = ArrayPool<T>.Shared.Rent(array.Length * 2);
        Array.Copy(array, bigger, array.Length);
        ArrayPool<T>.Shared.Return(array);
        array = bigger;
    }
}

/// <summary>One code-block of a precinct's subband. A struct inside the precinct-band array, so a tile's blocks cost one allocation per array.</summary>
internal struct J2kCodeBlock
{
    public int X0;
    public int Y0;
    public int X1;
    public int Y1;
    public bool Included;

    /// <summary>Number of coded magnitude bit-planes (known once the block is first included).</summary>
    public int BitPlanes;

    /// <summary>The Lblock state of the packet header length coding.</summary>
    public int LengthBits;

    /// <summary>First and last codeword segment in the tile's arena, or -1.</summary>
    public int FirstSegment;
    public int LastSegment;

    public readonly int Width => X1 - X0;

    public readonly int Height => Y1 - Y0;
}

/// <summary>The code-blocks of one subband inside one precinct, with the tag trees that code their inclusion and zero bit-planes.</summary>
internal sealed class J2kPrecinctBand
{
    public int BlocksWide { get; init; }

    public int BlocksHigh { get; init; }

    public required J2kCodeBlock[] Blocks { get; init; }

    public J2kTagTree? Inclusion { get; init; }

    public J2kTagTree? ZeroBitPlanes { get; init; }
}

/// <summary>A subband of one resolution level.</summary>
internal sealed class J2kBand
{
    /// <summary>0 LL, 1 HL, 2 LH, 3 HH.</summary>
    public int Orientation { get; init; }

    public int X0 { get; init; }

    public int Y0 { get; init; }

    public int X1 { get; init; }

    public int Y1 { get; init; }

    /// <summary>Magnitude bit-planes Mb (guard bits + exponent - 1), plus the region-of-interest shift.</summary>
    public int BitPlanes { get; init; }

    /// <summary>Dequantization step for the irreversible transform (1/2 of it is applied to the half-unit tier-1 output).</summary>
    public float StepSize { get; init; }

    /// <summary>Where the subband's samples go inside the tile-component buffer.</summary>
    public int OffsetX { get; init; }

    public int OffsetY { get; init; }

    public required J2kPrecinctBand[] Precincts { get; init; }
}

/// <summary>A resolution level of a tile-component.</summary>
internal sealed class J2kResolution
{
    public int X0 { get; init; }

    public int Y0 { get; init; }

    public int X1 { get; init; }

    public int Y1 { get; init; }

    public int PrecinctWidthExponent { get; init; }

    public int PrecinctHeightExponent { get; init; }

    public int PrecinctsWide { get; init; }

    public int PrecinctsHigh { get; init; }

    public required J2kBand[] Bands { get; init; }

    public int Width => X1 - X0;

    public int Height => Y1 - Y0;

    /// <summary>The next quality layer each precinct expects (layers of one precinct must arrive in order).</summary>
    public required int[] NextLayer { get; init; }
}

/// <summary>A component of a tile, with its coding parameters and its resolution levels.</summary>
internal sealed class J2kTileComponent
{
    public int X0 { get; init; }

    public int Y0 { get; init; }

    public int X1 { get; init; }

    public int Y1 { get; init; }

    public required J2kComponentCoding Coding { get; init; }

    public required J2kQuantization Quantization { get; init; }

    public int RoiShift { get; init; }

    public required J2kResolution[] Resolutions { get; init; }

    public int Width => X1 - X0;

    public int Height => Y1 - Y0;

    /// <summary>Reversible-transform samples (null when irreversible).</summary>
    public int[]? IntegerData { get; set; }

    /// <summary>Irreversible-transform samples (null when reversible).</summary>
    public float[]? FloatData { get; set; }

    public int ResolutionCount => Coding.Levels + 1;

    /// <summary>Builds the subband, precinct and code-block structure of one tile-component (T.800 Annex B).</summary>
    public static J2kTileComponent Build(
        J2kSiz siz,
        int componentIndex,
        int tx0,
        int ty0,
        int tx1,
        int ty1,
        J2kComponentCoding coding,
        J2kQuantization quantization,
        int roiShift)
    {
        var info = siz.Components[componentIndex];
        int tcx0 = CeilDiv(tx0, info.XRsiz);
        int tcy0 = CeilDiv(ty0, info.YRsiz);
        int tcx1 = CeilDiv(tx1, info.XRsiz);
        int tcy1 = CeilDiv(ty1, info.YRsiz);
        int levels = coding.Levels;
        var resolutions = new J2kResolution[levels + 1];
        long blockBudget = Jpeg2000DecodingLimits.MaxCodeBlocksPerTile;

        for (int r = 0; r <= levels; r++)
        {
            int levelNo = levels - r;
            int trx0 = CeilDivPow2(tcx0, levelNo);
            int try0 = CeilDivPow2(tcy0, levelNo);
            int trx1 = CeilDivPow2(tcx1, levelNo);
            int try1 = CeilDivPow2(tcy1, levelNo);
            int ppx = coding.PrecinctWidthExponent(r);
            int ppy = coding.PrecinctHeightExponent(r);
            int pw = trx1 > trx0 ? CeilDivPow2(trx1, ppx) - (trx0 >> ppx) : 0;
            int ph = try1 > try0 ? CeilDivPow2(try1, ppy) - (try0 >> ppy) : 0;
            int precinctCount = pw * ph;

            int bandCount = r == 0 ? 1 : 3;
            var bands = new J2kBand[bandCount];
            for (int b = 0; b < bandCount; b++)
            {
                int orientation = r == 0 ? 0 : b + 1;
                int xob = orientation is 1 or 3 ? 1 : 0;
                int yob = orientation is 2 or 3 ? 1 : 0;
                int nb = r == 0 ? levels : levels - r + 1;
                int bx0, by0, bx1, by1;
                if (r == 0)
                {
                    bx0 = trx0;
                    by0 = try0;
                    bx1 = trx1;
                    by1 = try1;
                }
                else
                {
                    long half = 1L << (nb - 1);
                    bx0 = (int)CeilDivLong(tcx0 - (half * xob), 1L << nb);
                    by0 = (int)CeilDivLong(tcy0 - (half * yob), 1L << nb);
                    bx1 = (int)CeilDivLong(tcx1 - (half * xob), 1L << nb);
                    by1 = (int)CeilDivLong(tcy1 - (half * yob), 1L << nb);
                }

                // Quantization parameters for this subband.
                int bandIndex = r == 0 ? 0 : (3 * (r - 1)) + b + 1;
                int exponent;
                int mantissa;
                if (quantization.Style == 1)
                {
                    exponent = Math.Max(0, quantization.Exponents[0] - (r == 0 ? 0 : r - 1));
                    mantissa = quantization.Mantissas[0];
                }
                else
                {
                    if (bandIndex >= quantization.Exponents.Length)
                    {
                        throw new Jpeg2000DecodingException("JPEG 2000 quantization marker lacks values for every subband.");
                    }

                    exponent = quantization.Exponents[bandIndex];
                    mantissa = quantization.Mantissas[bandIndex];
                }

                int bitPlanes = quantization.GuardBits + exponent - 1 + roiShift;
                if (bitPlanes < 0 || bitPlanes > 37)
                {
                    throw new Jpeg2000DecodingException("JPEG 2000 declares an invalid number of magnitude bit-planes.");
                }

                float step = 1f;
                if (!coding.Reversible || quantization.Style != 0)
                {
                    int gainLog2 = orientation == 0 ? 0 : orientation == 3 ? 2 : 1;
                    int range = info.Depth + gainLog2;
                    step = (1f + (mantissa / 2048f)) * MathF.Pow(2f, range - exponent);
                }

                int offsetX = 0;
                int offsetY = 0;
                if (r > 0)
                {
                    offsetX = xob == 1 ? resolutions[r - 1].Width : 0;
                    offsetY = yob == 1 ? resolutions[r - 1].Height : 0;
                }

                // Precinct and code-block partition of this subband.
                int bandPpx = r == 0 ? ppx : ppx - 1;
                int bandPpy = r == 0 ? ppy : ppy - 1;
                int cbw = Math.Min(coding.CodeBlockWidthExponent, bandPpx);
                int cbh = Math.Min(coding.CodeBlockHeightExponent, bandPpy);
                var precincts = new J2kPrecinctBand[precinctCount];
                for (int p = 0; p < precinctCount; p++)
                {
                    int px = p % pw;
                    int py = p / pw;
                    long pbx0 = Math.Max(bx0, ((long)((trx0 >> ppx) + px)) << bandPpx);
                    long pby0 = Math.Max(by0, ((long)((try0 >> ppy) + py)) << bandPpy);
                    long pbx1 = Math.Min(bx1, ((long)((trx0 >> ppx) + px + 1)) << bandPpx);
                    long pby1 = Math.Min(by1, ((long)((try0 >> ppy) + py + 1)) << bandPpy);
                    if (pbx1 <= pbx0 || pby1 <= pby0)
                    {
                        precincts[p] = new J2kPrecinctBand { BlocksWide = 0, BlocksHigh = 0, Blocks = [] };
                        continue;
                    }

                    int cx0 = (int)(pbx0 >> cbw);
                    int cy0 = (int)(pby0 >> cbh);
                    int cx1 = (int)((pbx1 + (1L << cbw) - 1) >> cbw);
                    int cy1 = (int)((pby1 + (1L << cbh) - 1) >> cbh);
                    int blocksWide = cx1 - cx0;
                    int blocksHigh = cy1 - cy0;
                    blockBudget -= (long)blocksWide * blocksHigh;
                    if (blockBudget < 0)
                    {
                        throw new Jpeg2000DecodingException("JPEG 2000 tile declares too many code-blocks.");
                    }

                    var blocks = new J2kCodeBlock[blocksWide * blocksHigh];
                    for (int j = 0; j < blocksHigh; j++)
                    {
                        for (int i = 0; i < blocksWide; i++)
                        {
                            long x0 = Math.Max(pbx0, (long)(cx0 + i) << cbw);
                            long y0 = Math.Max(pby0, (long)(cy0 + j) << cbh);
                            long x1 = Math.Min(pbx1, (long)(cx0 + i + 1) << cbw);
                            long y1 = Math.Min(pby1, (long)(cy0 + j + 1) << cbh);
                            blocks[(j * blocksWide) + i] = new J2kCodeBlock { X0 = (int)x0, Y0 = (int)y0, X1 = (int)x1, Y1 = (int)y1, LengthBits = 3, FirstSegment = -1, LastSegment = -1 };
                        }
                    }

                    precincts[p] = new J2kPrecinctBand
                    {
                        BlocksWide = blocksWide,
                        BlocksHigh = blocksHigh,
                        Blocks = blocks,
                        Inclusion = new J2kTagTree(blocksWide, blocksHigh),
                        ZeroBitPlanes = new J2kTagTree(blocksWide, blocksHigh),
                    };
                }

                bands[b] = new J2kBand
                {
                    Orientation = orientation,
                    X0 = bx0,
                    Y0 = by0,
                    X1 = bx1,
                    Y1 = by1,
                    BitPlanes = bitPlanes,
                    StepSize = step,
                    OffsetX = offsetX,
                    OffsetY = offsetY,
                    Precincts = precincts,
                };
            }

            resolutions[r] = new J2kResolution
            {
                X0 = trx0,
                Y0 = try0,
                X1 = trx1,
                Y1 = try1,
                PrecinctWidthExponent = ppx,
                PrecinctHeightExponent = ppy,
                PrecinctsWide = pw,
                PrecinctsHigh = ph,
                Bands = bands,
                NextLayer = new int[precinctCount],
            };
        }

        return new J2kTileComponent
        {
            X0 = tcx0,
            Y0 = tcy0,
            X1 = tcx1,
            Y1 = tcy1,
            Coding = coding,
            Quantization = quantization,
            RoiShift = roiShift,
            Resolutions = resolutions,
        };
    }

    public static int CeilDiv(int a, int b) => (int)(((long)a + b - 1) / b);

    public static int CeilDivPow2(int a, int n) => n >= 31 ? (a > 0 ? 1 : 0) : (int)(((long)a + (1L << n) - 1) >> n);

    private static long CeilDivLong(long a, long b)
    {
        long q = a / b;
        return a % b != 0 && ((a < 0) == (b < 0)) ? q + 1 : q;
    }
}
