using System.Numerics;
using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Entropy;
using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Jxl.Modular;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>One pass's section data and the current read position within it.</summary>
internal struct PassStream
{
    public ReadOnlyMemory<byte> Data;
    public long BitPosition;
}

/// <summary>
/// The decoding state of one VarDCT frame: quantization parameters, the DC image, the per-block metadata (transform type,
/// quantization, chroma-from-luma, edge-filter sharpness) and the reconstructed XYB planes. Sections are decoded into it in
/// the order the format defines (global, DC groups, AC global, AC groups), then <see cref="FilterToXyb"/> produces the XYB planes.
/// </summary>
internal sealed partial class VarDctFrame : IQuantTableReader
{
    private const int MaxQuantValue = 256;
    private const int GroupDimInBlocks = 32;
    private const byte InvalidStrategy = 0xFF;

    private static readonly int[] KCoeffFreqContext =
    [
        0xBAD, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
        15, 15, 16, 16, 17, 17, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22,
        23, 23, 23, 23, 24, 24, 24, 24, 25, 25, 25, 25, 26, 26, 26, 26,
        27, 27, 27, 27, 28, 28, 28, 28, 29, 29, 29, 29, 30, 30, 30, 30,
    ];

    private static readonly int[] KCoeffNumNonzeroContext =
    [
        0xBAD, 0, 31, 62, 62, 93, 93, 93, 93, 123, 123, 123, 123,
        152, 152, 152, 152, 152, 152, 152, 152, 180, 180, 180, 180, 180,
        180, 180, 180, 180, 180, 180, 180, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
    ];

    private readonly JxlFrameHeader _frame;
    private readonly JxlCodestreamHeaders _headers;
    private readonly JxlFrameDimensions _dims;
    private readonly int _blocksX;
    private readonly int _blocksY;

    // Chroma subsampling shifts per plane (0 = full resolution). Only JPEG-derived frames subsample; XYB frames never do.
    private static readonly int[] ModeHorizontalShift = [0, 1, 1, 0];
    private static readonly int[] ModeVerticalShift = [0, 1, 0, 1];
    private readonly int[] _hShift = new int[3];
    private readonly int[] _vShift = new int[3];
    private readonly int[] _dcStride = new int[3];
    private readonly float[][] _dc = new float[3][];
    private readonly byte[] _quantDcBucket;
    private readonly int[] _rawQuant;
    private readonly byte[] _acStrategy;
    private readonly byte[] _epfSharpness;
    private readonly sbyte[] _yToX;
    private readonly sbyte[] _yToB;
    private readonly int _tilesX;

    private uint _usedAcs;
    private int _numHistograms;
    private JxlEntropyCode[] _acCodes = [];
    private byte[][] _acContextMaps = [];
    private int[][][][] _coeffOrders = []; // [pass][order][channel] -> position table

    private ModularContextModel? _globalModel;
    private float[][]? _planes;

    public VarDctFrame(JxlFrameHeader frame, JxlCodestreamHeaders headers)
    {
        _frame = frame;
        _headers = headers;
        _dims = frame.Dimensions;
        _blocksX = _dims.XSizeBlocks;
        _blocksY = _dims.YSizeBlocks;
        int blocks = _blocksX * _blocksY;
        int maxH = 0, maxV = 0;
        for (int c = 0; c < 3; c++) { maxH = Math.Max(maxH, ModeHorizontalShift[frame.ChromaSubsamplingModes[c]]); maxV = Math.Max(maxV, ModeVerticalShift[frame.ChromaSubsamplingModes[c]]); }
        for (int c = 0; c < 3; c++)
        {
            int mode = frame.ChromaSubsamplingModes[c];
            _hShift[c] = maxH - ModeHorizontalShift[mode];
            _vShift[c] = maxV - ModeVerticalShift[mode];
            _dcStride[c] = _blocksX >> _hShift[c];
            _dc[c] = new float[blocks];
        }

        _quantDcBucket = new byte[blocks];
        _rawQuant = new int[blocks];
        _acStrategy = new byte[blocks];
        Array.Fill(_acStrategy, InvalidStrategy);
        _epfSharpness = new byte[blocks];
        _tilesX = (_blocksX + ColorCorrelation.TileDimInBlocks - 1) / ColorCorrelation.TileDimInBlocks;
        int tilesY = (_blocksY + ColorCorrelation.TileDimInBlocks - 1) / ColorCorrelation.TileDimInBlocks;
        _yToX = new sbyte[_tilesX * tilesY];
        _yToB = new sbyte[_tilesX * tilesY];
    }

    /// <summary>Whether all three channels have the same resolution (no chroma subsampling).</summary>
    public bool IsFullResolution => _hShift[0] == 0 && _vShift[0] == 0 && _hShift[1] == 0 && _vShift[1] == 0 && _hShift[2] == 0 && _vShift[2] == 0;

    public VarDctQuantizer Quantizer { get; private set; } = null!;

    public BlockContextMap BlockContext { get; private set; } = new();

    public ColorCorrelation Correlation { get; } = new();

    public DequantMatrices Matrices { get; } = new();

    /// <summary>Takes the DC image from a previously decoded DC frame instead of from this frame's DC groups.</summary>
    public void UseDcFrame(Features.JxlReferenceFrame dc)
    {
        if (dc.Width != _blocksX || dc.Height != _blocksY)
        {
            throw new JxlDecodingException("The DC frame does not match the frame's size in blocks.");
        }

        for (int c = 0; c < 3; c++)
        {
            Array.Copy(dc.Planes[c], _dc[c], _dc[c].Length);
        }
    }

    /// <summary>Sets the frame-wide Modular model (the global MA tree) that DC-group and RAW-table streams may use.</summary>
    public void SetGlobalModel(ModularContextModel? model) => _globalModel = model;

    /// <summary>Reads the VarDCT part of the DC global section (quantizer, block contexts, chroma-from-luma).</summary>
    public void ReadGlobal(ref JxlBitReader br)
    {
        Quantizer = VarDctQuantizer.Read(ref br);
        BlockContext = BlockContextMap.Read(ref br);
        var lf = _frame.LoopFilter;
        Correlation.Read(ref br);
        br.ThrowIfOverrun();
    }

    private float[] MulDc
    {
        get
        {
            var mul = new float[3];
            for (int c = 0; c < 3; c++)
            {
                mul[c] = Quantizer.InverseQuantDc * Matrices.DcQuant[c];
            }

            return mul;
        }
    }

    private (int X0, int Y0, int Width, int Height) DcGroupRect(int group)
    {
        int gx = group % _dims.XSizeDcGroups;
        int gy = group / _dims.XSizeDcGroups;
        int dim = _dims.GroupDimension;
        int x0 = gx * dim;
        int y0 = gy * dim;
        return (x0, y0, Math.Min(dim, _blocksX - x0), Math.Min(dim, _blocksY - y0));
    }

    /// <summary>Decodes the quantized DC of a DC group and dequantizes it into the DC image.</summary>
    public void DecodeDc(ref JxlBitReader br, int group, int bitDepth)
    {
        var (x0, y0, w, h) = DcGroupRect(group);
        uint extraPrecision = br.ReadBits(2);
        float mul = 1.0f / (1 << (int)extraPrecision);
        using var image = new ModularImage(w, h, bitDepth);

        // Modular channel order is Y, X, B; each channel has the size of its plane (subsampled chroma has fewer blocks).
        for (int m = 0; m < 3; m++)
        {
            int c = m < 2 ? m ^ 1 : m;
            image.Channels.Add(new ModularChannel(w >> _hShift[c], h >> _vShift[c]));
        }

        ModularStreamDecoder.Decode(ref br, image, (uint)(1 + group), 0xFFFFFF, 0x1FFFFFFF, _globalModel, undoTransforms: true);

        // A recompressed JPEG keeps its DC as plain quantized integers (only the extra-precision bits scale them).
        var factors = _jpeg is not null ? [1f, 1f, 1f] : MulDc;
        var qy = image.Channels[0];
        var qx = image.Channels[1];
        var qb = image.Channels[2];
        if (IsFullResolution)
        {
            float facX = factors[0] * mul;
            float facY = factors[1] * mul;
            float facB = factors[2] * mul;
            float cflX = Correlation.DcFactors[0];
            float cflB = Correlation.DcFactors[2];
            for (int y = 0; y < h; y++)
            {
                var rowX = qx.ReadOnlyRow(y);
                var rowY = qy.ReadOnlyRow(y);
                var rowB = qb.ReadOnlyRow(y);
                int dst = ((y0 + y) * _blocksX) + x0;
                for (int x = 0; x < w; x++)
                {
                    float inX = rowX[x] * facX;
                    float inY = rowY[x] * facY;
                    float inB = rowB[x] * facB;
                    _dc[1][dst + x] = inY;
                    _dc[0][dst + x] = (inY * cflX) + inX;
                    _dc[2][dst + x] = (inY * cflB) + inB;
                }
            }
        }
        else
        {
            // Subsampled chroma: no chroma-from-luma, and each plane is dequantized at its own resolution.
            ModularChannel[] byPlane = [qx, qy, qb];
            for (int c = 0; c < 3; c++)
            {
                float fac = factors[c] * mul;
                var channel = byPlane[c];
                int planeX0 = x0 >> _hShift[c];
                int planeY0 = y0 >> _vShift[c];
                for (int y = 0; y < channel.Height; y++)
                {
                    var row = channel.ReadOnlyRow(y);
                    int dst = ((planeY0 + y) * _dcStride[c]) + planeX0;
                    for (int x = 0; x < channel.Width; x++)
                    {
                        _dc[c][dst + x] = row[x] * fac;
                    }
                }
            }
        }

        // The DC bucket selects block contexts when thresholds are configured.
        var bctx = BlockContext;
        if (bctx.NumDcContexts > 1)
        {
            for (int y = 0; y < h; y++)
            {
                var rowX = qx.ReadOnlyRow(y >> _vShift[0]);
                var rowY = qy.ReadOnlyRow(y >> _vShift[1]);
                var rowB = qb.ReadOnlyRow(y >> _vShift[2]);
                int dst = ((y0 + y) * _blocksX) + x0;
                for (int x = 0; x < w; x++)
                {
                    int bucketX = 0;
                    int bucketY = 0;
                    int bucketB = 0;
                    foreach (int t in bctx.DcThresholds[0])
                    {
                        if (rowX[x >> _hShift[0]] > t)
                        {
                            bucketX++;
                        }
                    }

                    foreach (int t in bctx.DcThresholds[1])
                    {
                        if (rowY[x >> _hShift[1]] > t)
                        {
                            bucketY++;
                        }
                    }

                    foreach (int t in bctx.DcThresholds[2])
                    {
                        if (rowB[x >> _hShift[2]] > t)
                        {
                            bucketB++;
                        }
                    }

                    int bucket = bucketX;
                    bucket *= bctx.DcThresholds[2].Length + 1;
                    bucket += bucketB;
                    bucket *= bctx.DcThresholds[1].Length + 1;
                    bucket += bucketY;
                    _quantDcBucket[dst + x] = (byte)bucket;
                }
            }
        }
    }

    /// <summary>Decodes the per-block AC metadata of a DC group: chroma-from-luma maps, transform types, quantization and EPF sharpness.</summary>
    public void DecodeAcMetadata(ref JxlBitReader br, int group, int bitDepth)
    {
        var (x0, y0, w, h) = DcGroupRect(group);
        int upperBound = w * h;
        int bits = upperBound <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(upperBound - 1));
        int count = (int)br.ReadBits(bits) + 1;

        int tileX0 = x0 >> 3;
        int tileY0 = y0 >> 3;
        int tileW = (w + 7) >> 3;
        int tileH = (h + 7) >> 3;

        using var image = new ModularImage(w, h, bitDepth);
        image.Channels.Add(new ModularChannel(tileW, tileH, 3, 3));
        image.Channels.Add(new ModularChannel(tileW, tileH, 3, 3));
        image.Channels.Add(new ModularChannel(count, 2, 0, 0));
        image.Channels.Add(new ModularChannel(w, h));
        ModularStreamDecoder.Decode(ref br, image, (uint)(1 + (2 * _dims.NumDcGroups) + group), 0xFFFFFF, 0x1FFFFFFF, _globalModel, undoTransforms: true);

        for (int ty = 0; ty < tileH; ty++)
        {
            var rowX = image.Channels[0].ReadOnlyRow(ty);
            var rowB = image.Channels[1].ReadOnlyRow(ty);
            for (int tx = 0; tx < tileW; tx++)
            {
                int idx = ((tileY0 + ty) * _tilesX) + tileX0 + tx;
                _yToX[idx] = (sbyte)Math.Clamp(rowX[tx], sbyte.MinValue, sbyte.MaxValue);
                _yToB[idx] = (sbyte)Math.Clamp(rowB[tx], sbyte.MinValue, sbyte.MaxValue);
            }
        }
        var strategyRow = image.Channels[2].ReadOnlyRow(0);
        var quantRow = image.Channels[2].ReadOnlyRow(1);
        var sharpness = image.Channels[3];
        int num = 0;
        int xLimit = Math.Min(_blocksX, x0 + w);
        int yLimit = Math.Min(_blocksY, y0 + h);
        for (int iy = 0; iy < h; iy++)
        {
            int y = y0 + iy;
            var sharpRow = sharpness.ReadOnlyRow(iy);
            for (int ix = 0; ix < w; ix++)
            {
                int x = x0 + ix;
                int index = (y * _blocksX) + x;
                int sharp = sharpRow[ix];
                if ((uint)sharp >= JxlLoopFilter.EpfSharpEntries)
                {
                    throw new JxlDecodingException("Corrupted EPF sharpness field.");
                }

                _epfSharpness[index] = (byte)sharp;
                if (_acStrategy[index] != InvalidStrategy)
                {
                    continue;
                }

                if (num >= count)
                {
                    throw new JxlDecodingException("Corrupted AC metadata.");
                }

                int raw = strategyRow[num];
                if (!AcStrategy.IsValid(raw))
                {
                    throw new JxlDecodingException("Invalid AC strategy.");
                }

                _usedAcs |= 1u << raw;
                int cx = AcStrategy.CoveredBlocksX(raw);
                int cy = AcStrategy.CoveredBlocksY(raw);
                if ((cx > 1 || cy > 1) && !IsFullResolution)
                {
                    throw new JxlDecodingException("An AC strategy is not compatible with chroma subsampling.");
                }

                // A transform must not straddle an AC group or the image edge.
                int nextXAcBlock = ((x / GroupDimInBlocks) + 1) * GroupDimInBlocks;
                int nextYAcBlock = ((y / GroupDimInBlocks) + 1) * GroupDimInBlocks;
                if (x + cx > nextXAcBlock || x + cx > xLimit)
                {
                    throw new JxlDecodingException("Invalid AC strategy: x overflow.");
                }

                if (y + cy > nextYAcBlock || y + cy > yLimit)
                {
                    throw new JxlDecodingException("Invalid AC strategy: y overflow.");
                }

                for (int jy = 0; jy < cy; jy++)
                {
                    for (int jx = 0; jx < cx; jx++)
                    {
                        int cell = ((y + jy) * _blocksX) + x + jx;
                        if (_acStrategy[cell] != InvalidStrategy)
                        {
                            throw new JxlDecodingException("Invalid AC strategy: block overlap.");
                        }

                        _acStrategy[cell] = (byte)((raw << 1) | (jy == 0 && jx == 0 ? 1 : 0));
                    }
                }

                _rawQuant[index] = 1 + Math.Clamp(quantRow[num], 0, MaxQuantValue - 1);
                num++;
            }
        }
    }

    /// <summary>Smooths the DC image where it is locally close to a smooth ramp (a decoder-side deblocking of the DC).</summary>
    public void AdaptiveDcSmoothing()
    {
        if (_blocksX <= 2 || _blocksY <= 2)
        {
            return;
        }

        const float w1 = 0.20345139757231578f;
        const float w2 = 0.0334829185968739f;
        const float w0 = 1.0f - (4.0f * (w1 + w2));
        float[] factors = MulDc;
        var smoothed = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            smoothed[c] = (float[])_dc[c].Clone();
        }

        var sm = new float[3];
        for (int y = 1; y < _blocksY - 1; y++)
        {
            for (int x = 1; x < _blocksX - 1; x++)
            {
                float gap = 0.5f;
                int i = (y * _blocksX) + x;
                for (int c = 0; c < 3; c++)
                {
                    var p = _dc[c];
                    float mc = p[i];
                    float corner = p[i - _blocksX - 1] + p[i - _blocksX + 1] + p[i + _blocksX - 1] + p[i + _blocksX + 1];
                    float side = p[i - 1] + p[i + 1] + p[i - _blocksX] + p[i + _blocksX];
                    sm[c] = (corner * w2) + ((side * w1) + (mc * w0));
                    gap = MathF.Max(gap, MathF.Abs((mc - sm[c]) / factors[c]));
                }

                float factor = MathF.Max(0f, (-4.0f * gap) + 3.0f);
                for (int c = 0; c < 3; c++)
                {
                    float mc = _dc[c][i];
                    smoothed[c][i] = ((sm[c] - mc) * factor) + mc;
                }
            }
        }

        for (int c = 0; c < 3; c++)
        {
            _dc[c] = smoothed[c];
        }
    }

    /// <summary>Reads the AC global section: dequantization tables, histogram-set count, and per pass the coefficient orders and AC histograms.</summary>
    public void ReadAcGlobal(ref JxlBitReader br)
    {
        Matrices.Read(ref br, this);

        int groupBits = _dims.NumGroups <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(_dims.NumGroups - 1));
        _numHistograms = 1 + (int)br.ReadBits(groupBits);

        int passes = _frame.NumPasses;
        _acCodes = new JxlEntropyCode[passes];
        _acContextMaps = new byte[passes][];
        _coeffOrders = new int[passes][][][];
        for (int pass = 0; pass < passes; pass++)
        {
            uint usedOrders = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0x5F), U32Dist.Val(0x13), U32Dist.Val(0), U32Dist.Bits(13));
            _coeffOrders[pass] = ReadCoeffOrders(ref br, usedOrders);
            int numContexts = _numHistograms * BlockContext.NumAcContexts;
            _acCodes[pass] = JxlEntropyCode.Read(ref br, numContexts, out _acContextMaps[pass]);
        }

        br.ThrowIfOverrun();
    }

    private int[][][] ReadCoeffOrders(ref JxlBitReader br, uint usedOrders)
    {
        var orders = new int[AcStrategy.NumOrders][][];
        JxlSymbolReader? reader = null;
        byte[] contextMap = [];
        try
        {
            if (usedOrders != 0)
            {
                var code = JxlEntropyCode.Read(ref br, JxlPermutation.NumContexts, out contextMap);
                reader = new JxlSymbolReader(code, ref br);
            }

            uint acsMask = 0;
            for (int o = 0; o < AcStrategy.Count; o++)
            {
                if ((_usedAcs & (1u << o)) != 0)
                {
                    acsMask |= 1u << AcStrategy.OrderOf(o);
                }
            }

            uint computed = 0;
            for (int o = 0; o < AcStrategy.Count; o++)
            {
                int ord = AcStrategy.OrderOf(o);
                if ((computed & (1u << ord)) != 0)
                {
                    continue;
                }

                computed |= 1u << ord;
                bool used = (acsMask & (1u << ord)) != 0;
                int llf = AcStrategy.CoveredBlocksX(o) * AcStrategy.CoveredBlocksY(o);
                int size = AcStrategy.BlockSize * llf;
                int[]? natural = used || (usedOrders & (1u << ord)) != 0 ? AcStrategy.NaturalOrder(o) : null;

                if ((usedOrders & (1u << ord)) == 0)
                {
                    if (used)
                    {
                        orders[ord] = [natural!, natural!, natural!];
                    }
                }
                else
                {
                    var perChannel = new int[3][];
                    for (int c = 0; c < 3; c++)
                    {
                        int[] permutation = JxlPermutation.Read(ref br, reader!, contextMap, llf, size);

                        if (used)
                        {
                            var order = new int[size];
                            for (int k = 0; k < size; k++)
                            {
                                order[k] = natural![permutation[k]];
                            }

                            perChannel[c] = order;
                        }
                    }

                    if (used)
                    {
                        orders[ord] = perChannel;
                    }
                }
            }

            if (reader is not null && !reader.CheckFinalState())
            {
                throw new JxlDecodingException("Invalid ANS stream for the coefficient orders.");
            }

            br.ThrowIfOverrun();
            return orders;
        }
        finally
        {
            reader?.Dispose();
        }
    }

    // RAW quantization tables are tiny Modular images decoded with the frame's global tree.
    int[] IQuantTableReader.ReadRawTable(ref JxlBitReader br, int width, int height, int index)
    {
        using var image = new ModularImage(width, height, 8);
        image.AddChannels(3);
        ModularStreamDecoder.Decode(
            ref br,
            image,
            (uint)(1 + (3 * _dims.NumDcGroups) + index),
            0xFFFFFF,
            0x1FFFFFFF,
            _globalModel,
            undoTransforms: true);

        var table = new int[3 * width * height];
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < height; y++)
            {
                var row = image.Channels[c].ReadOnlyRow(y);
                for (int x = 0; x < width; x++)
                {
                    int value = row[x];
                    if (value <= 0)
                    {
                        throw new JxlDecodingException("Invalid raw quantization table.");
                    }

                    table[(c * width * height) + (y * width) + x] = value;
                }
            }
        }

        return table;
    }

    private void EnsurePlanes()
    {
        if (_planes is not null)
        {
            return;
        }

        if (_jpeg is not null)
        {
            // Nothing is rendered when the blocks are only captured as JPEG coefficients.
            PrepareJpeg();
            _planes = new float[3][];
            return;
        }

        // Rented, not cleared: every block of the padded grid is written by the group decode before any pixel is read.
        var pool = System.Buffers.ArrayPool<float>.Shared;
        var planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            planes[c] = pool.Rent((_dims.XSizePadded >> _hShift[c]) * (_dims.YSizePadded >> _vShift[c]));
        }

        _planes = planes;
    }

    /// <summary>Creates the lazily-built shared state (output planes, dequantization tables) so that groups can then be decoded concurrently.</summary>
    public void PrepareParallelDecode()
    {
        EnsurePlanes();
        for (int o = 0; o < AcStrategy.Count; o++)
        {
            if ((_usedAcs & (1u << o)) != 0)
            {
                Matrices.Matrix(o);
            }
        }
    }

    /// <summary>
    /// Decodes the AC coefficients of one AC group from every pass's stream, then dequantizes and inverse-transforms its blocks
    /// into the XYB planes. <paramref name="streams"/> holds one stream per pass; their bit positions are advanced past the data read.
    /// </summary>
    public void DecodeAcGroup(int group, PassStream[] streams)
    {
        EnsurePlanes();
        var planes = _planes!;
        int passes = _frame.NumPasses;
        int gx = group % _dims.XSizeGroups;
        int gy = group / _dims.XSizeGroups;
        int groupBlocks = _dims.GroupDimension >> 3;
        int bx0 = gx * groupBlocks;
        int by0 = gy * groupBlocks;
        int groupW = Math.Min(groupBlocks, _blocksX - bx0);
        int groupH = Math.Min(groupBlocks, _blocksY - by0);

        int histogramBits = _numHistograms <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(_numHistograms - 1));
        var symbolReaders = new JxlSymbolReader[passes];
        var contextOffsets = new int[passes];
        try
        {
            for (int pass = 0; pass < passes; pass++)
            {
                var br = new JxlBitReader(streams[pass].Data.Span, streams[pass].BitPosition);
                int histogram = histogramBits == 0 ? 0 : (int)br.ReadBits(histogramBits);
                if (histogram >= _numHistograms)
                {
                    throw new JxlDecodingException("Invalid histogram selector.");
                }

                contextOffsets[pass] = histogram * BlockContext.NumAcContexts;
                symbolReaders[pass] = new JxlSymbolReader(_acCodes[pass], ref br);
                br.ThrowIfOverrun();
                streams[pass].BitPosition = br.BitPosition;
            }

            DecodeGroupBlocks(planes, bx0, by0, groupW, groupH, streams, symbolReaders, contextOffsets);

            for (int pass = 0; pass < passes; pass++)
            {
                if (!symbolReaders[pass].CheckFinalState())
                {
                    throw new JxlDecodingException("ANS checksum failure in an AC group.");
                }
            }
        }
        finally
        {
            foreach (var reader in symbolReaders)
            {
                reader?.Dispose();
            }
        }
    }

    private void DecodeGroupBlocks(
        float[][] planes,
        int bx0,
        int by0,
        int groupW,
        int groupH,
        PassStream[] streams,
        JxlSymbolReader[] symbolReaders,
        int[] contextOffsets)
    {
        int passes = _frame.NumPasses;
        float xDmMultiplier = MathF.Pow(1 / 1.25f, _frame.XQmScale - 2.0f);
        float bDmMultiplier = MathF.Pow(1 / 1.25f, _frame.BQmScale - 2.0f);
        float[] biases = _headers.TransformData.QuantBiases;

        // Per pass and channel: the number of non-zero coefficients of each decoded block (for context prediction).
        var nonZeros = new int[passes][][];
        for (int pass = 0; pass < passes; pass++)
        {
            nonZeros[pass] = [new int[GroupDimInBlocks * GroupDimInBlocks], new int[GroupDimInBlocks * GroupDimInBlocks], new int[GroupDimInBlocks * GroupDimInBlocks]];
        }

        int maxSize = 0;
        for (int o = 0; o < AcStrategy.Count; o++)
        {
            if ((_usedAcs & (1u << o)) != 0)
            {
                maxSize = Math.Max(maxSize, AcStrategy.CoveredBlocksX(o) * AcStrategy.CoveredBlocksY(o) * AcStrategy.BlockSize);
            }
        }

        var quantized = new int[3][];
        var dequantized = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            quantized[c] = new int[maxSize];
            dequantized[c] = new float[maxSize];
        }

        var scratch = new float[Math.Max(5 * maxSize, 8 * 64 * 4)];
        var pixelBlock = new float[maxSize];
        var present = new bool[3];

        for (int by = 0; by < groupH; by++)
        {
            for (int bx = 0; bx < groupW;)
            {
                int blockIndex = ((by0 + by) * _blocksX) + bx0 + bx;
                byte entry = _acStrategy[blockIndex];
                if (entry == InvalidStrategy)
                {
                    throw new JxlDecodingException("A block has no transform type.");
                }

                int strategy = entry >> 1;
                int cx = AcStrategy.CoveredBlocksX(strategy);
                int cy = AcStrategy.CoveredBlocksY(strategy);
                if ((entry & 1) == 0)
                {
                    // Not the first block of its transform (second or later row of a multi-block transform).
                    bx += cx;
                    continue;
                }

                int log2Covered = AcStrategy.Log2CoveredBlocks(strategy);
                int covered = 1 << log2Covered;
                int size = covered * AcStrategy.BlockSize;
                for (int c = 0; c < 3; c++)
                {
                    Array.Clear(quantized[c], 0, size);
                }

                int order = AcStrategy.OrderOf(strategy);
                int qdc = _quantDcBucket[blockIndex];

                // With chroma subsampling a subsampled channel only has a block at every other luma position.
                for (int c = 0; c < 3; c++)
                {
                    present[c] = ((bx & ((1 << _hShift[c]) - 1)) == 0) && ((by & ((1 << _vShift[c]) - 1)) == 0);
                }

                foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
                {
                    if (!present[c])
                    {
                        continue;
                    }

                    // The reference decoder indexes the quantization field with the channel-space column here.
                    int sbx = bx >> _hShift[c];
                    int sby = by >> _vShift[c];
                    uint qf = (uint)_rawQuant[((by0 + by) * _blocksX) + bx0 + sbx];
                    for (int pass = 0; pass < passes; pass++)
                    {
                        var br = new JxlBitReader(streams[pass].Data.Span, streams[pass].BitPosition);
                        DecodeAcBlock(
                            ref br,
                            symbolReaders[pass],
                            _acContextMaps[pass],
                            contextOffsets[pass],
                            nonZeros[pass][c],
                            sbx,
                            sby,
                            cx,
                            cy,
                            log2Covered,
                            _coeffOrders[pass][order][c],
                            order,
                            c,
                            qdc,
                            qf,
                            quantized[c],
                            _frame.PassShift[pass]);
                        streams[pass].BitPosition = br.BitPosition;
                    }
                }

                if (_jpeg is not null)
                {
                    if (strategy != (int)AcStrategyType.Dct)
                    {
                        throw new JxlDecodingException("A JPEG can only be reconstructed from frames that use the 8x8 DCT alone.");
                    }

                    CaptureJpegBlock(bx0 + bx, by0 + by, quantized, present);
                    bx += cx;
                    continue;
                }

                // Dequantize, add chroma-from-luma, and fill the lowest frequencies from DC.
                int tile = (((by0 + by) / ColorCorrelation.TileDimInBlocks) * _tilesX) + ((bx0 + bx) / ColorCorrelation.TileDimInBlocks);
                float xCc = IsFullResolution ? Correlation.YToXRatio(_yToX[tile]) : 0f;
                float bCc = IsFullResolution ? Correlation.YToBRatio(_yToB[tile]) : 0f;
                float scaledDequant = Quantizer.InverseGlobalScale / _rawQuant[blockIndex];
                float scaledX = scaledDequant * xDmMultiplier;
                float scaledB = scaledDequant * bDmMultiplier;
                float[] matrices = Matrices.Matrix(strategy);
                DequantKernel.Dequantize(
                    quantized[0],
                    quantized[1],
                    quantized[2],
                    matrices,
                    size,
                    scaledX,
                    scaledDequant,
                    scaledB,
                    biases,
                    xCc,
                    bCc,
                    dequantized[0],
                    dequantized[1],
                    dequantized[2]);

                foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
                {
                    if (!present[c])
                    {
                        continue;
                    }

                    int sbx = (bx0 + bx) >> _hShift[c];
                    int sby = (by0 + by) >> _vShift[c];
                    DctTransforms.LowestFrequenciesFromDc(strategy, _dc[c].AsSpan((sby * _dcStride[c]) + sbx), _dcStride[c], dequantized[c]);
                    int planeStride = _dims.XSizePadded >> _hShift[c];
                    int pixelOffset = (sby * 8 * planeStride) + (sbx * 8);
                    DctTransforms.TransformToPixels(strategy, dequantized[c].AsSpan(0, size), planes[c].AsSpan(pixelOffset), planeStride, scratch);
                }

                bx += cx;
            }
        }
    }

    private void DecodeAcBlock(
        ref JxlBitReader br,
        JxlSymbolReader reader,
        byte[] contextMap,
        int contextOffset,
        int[] nonZeroRow,
        int bx,
        int by,
        int coveredX,
        int coveredY,
        int log2Covered,
        int[] coeffOrder,
        int orderBucket,
        int channel,
        int qdc,
        uint qf,
        int[] block,
        int shift)
    {
        int covered = 1 << log2Covered;
        int size = covered * AcStrategy.BlockSize;

        // Predict the non-zero count from the blocks above and to the left.
        int predicted;
        if (bx == 0)
        {
            predicted = by == 0 ? 32 : nonZeroRow[((by - 1) * GroupDimInBlocks) + bx];
        }
        else if (by == 0)
        {
            predicted = nonZeroRow[(by * GroupDimInBlocks) + bx - 1];
        }
        else
        {
            predicted = (nonZeroRow[((by - 1) * GroupDimInBlocks) + bx] + nonZeroRow[(by * GroupDimInBlocks) + bx - 1] + 1) / 2;
        }

        int blockContext = BlockContext.Context(qdc, qf, orderBucket, channel);
        int nonZeroContext = BlockContext.NonZeroContext(predicted, blockContext) + contextOffset;
        uint nonZeros = reader.ReadHybridUint(CheckContext(contextMap, nonZeroContext), ref br);
        if (nonZeros > size - covered)
        {
            throw new JxlDecodingException($"Invalid AC data: {nonZeros} non-zero coefficients in {covered} block(s).");
        }

        int stored = (int)((nonZeros + (uint)covered - 1) >> log2Covered);
        for (int y = 0; y < coveredY; y++)
        {
            for (int x = 0; x < coveredX; x++)
            {
                nonZeroRow[((by + y) * GroupDimInBlocks) + bx + x] = stored;
            }
        }

        int histogramOffset = contextOffset + BlockContext.ZeroDensityContextsOffset(blockContext);
        int prev = nonZeros > (uint)(size / 16) ? 0 : 1;
        int remaining = (int)nonZeros;
        for (int k = covered; k < size && remaining != 0; k++)
        {
            int nonzerosLeft = (remaining + covered - 1) >> log2Covered;
            int ctx = histogramOffset + ((KCoeffNumNonzeroContext[nonzerosLeft] + KCoeffFreqContext[k >> log2Covered]) * 2) + prev;
            uint u = reader.ReadHybridUint(CheckContext(contextMap, ctx), ref br);
            int magnitude = (int)(u >> 1);
            int negSign = (int)(~u & 1);
            int coeff = (magnitude ^ (negSign - 1)) << shift;
            block[coeffOrder[k]] += coeff;
            prev = u != 0 ? 1 : 0;
            remaining -= prev;
        }

        if (remaining != 0)
        {
            throw new JxlDecodingException("Invalid AC data: non-zero count left at the end of a block.");
        }

        br.ThrowIfOverrun();
    }

    // Maps an AC context to its histogram, rejecting contexts a corrupt stream could push out of range.
    private static int CheckContext(byte[] contextMap, int context)
    {
        if ((uint)context >= (uint)contextMap.Length)
        {
            throw new JxlDecodingException("An AC context is out of range.");
        }

        return contextMap[context];
    }

    /// <summary>
    /// Finishes the transform stage: applies Gaborish and the edge-preserving filter. Returns the three XYB planes, each
    /// <see cref="JxlFrameDimensions.XSizePadded"/> floats wide; the caller adds features and converts to RGB.
    /// </summary>
    public float[][] FilterToXyb()
    {
        EnsurePlanes();

        var planes = _planes!;
        if (!IsFullResolution)
        {
            // Chroma planes were decoded at reduced resolution; bring them to the luma resolution first.
            for (int c = 0; c < 3; c++)
            {
                if (_hShift[c] == 0 && _vShift[c] == 0)
                {
                    continue;
                }

                int realWidth = (_dims.XSize + (1 << _hShift[c]) - 1) >> _hShift[c];
                int realHeight = (_dims.YSize + (1 << _vShift[c]) - 1) >> _vShift[c];
                float[] reduced = planes[c];
                planes[c] = ChromaUpsampling.Upsample(reduced, _dims.XSizePadded >> _hShift[c], realWidth, realHeight, _hShift[c], _vShift[c], _dims.XSizePadded, _dims.YSizePadded);
                System.Buffers.ArrayPool<float>.Shared.Return(reduced);
            }
        }

        int stride = _dims.XSizePadded;
        int width = _dims.XSize;
        int height = _dims.YSize;
        var lf = _frame.LoopFilter;

        float[]? sigma = lf.EpfIterations > 0
            ? FramePostProcessing.SigmaFromQuantField(lf, Quantizer.ScaleFloat, _blocksX, _blocksY, _rawQuant, _acStrategy, _epfSharpness)
            : null;
        RestorationFilters.Apply(planes, stride, width, height, lf, sigma, _blocksX);

        return planes;
    }
}
