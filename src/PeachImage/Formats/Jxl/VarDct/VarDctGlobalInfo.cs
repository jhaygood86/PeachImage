using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Entropy;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>Global quantization parameters: the AC quantization scale and the DC quantization step.</summary>
internal sealed class VarDctQuantizer
{
    private const int GlobalScaleDenominator = 1 << 16;

    private VarDctQuantizer(int globalScale, int quantDc)
    {
        GlobalScale = globalScale;
        QuantDc = quantDc;

        // Computed in double and narrowed, as the reference does.
        InverseGlobalScale = (float)(1.0 * GlobalScaleDenominator / globalScale);
        ScaleFloat = (float)(globalScale * (1.0 / GlobalScaleDenominator));
        InverseQuantDc = InverseGlobalScale / quantDc;
    }

    public int GlobalScale { get; }

    public int QuantDc { get; }

    /// <summary>The reciprocal of the global scale: multiplied by a dequantization matrix and divided by the per-block quant value.</summary>
    public float InverseGlobalScale { get; }

    /// <summary>The global scale as a fraction of 65536.</summary>
    public float ScaleFloat { get; }

    public float InverseQuantDc { get; }

    public static VarDctQuantizer Read(ref JxlBitReader br)
    {
        uint globalScale = JxlFieldReader.ReadU32(ref br, U32Dist.BitsOffset(11, 1), U32Dist.BitsOffset(11, 2049), U32Dist.BitsOffset(12, 4097), U32Dist.BitsOffset(16, 8193));
        uint quantDc = JxlFieldReader.ReadU32(ref br, U32Dist.Val(16), U32Dist.BitsOffset(5, 1), U32Dist.BitsOffset(8, 1), U32Dist.BitsOffset(16, 1));
        br.ThrowIfOverrun();
        if (globalScale == 0 || quantDc == 0)
        {
            throw new JxlDecodingException("Invalid quantizer parameters.");
        }

        return new VarDctQuantizer((int)globalScale, (int)quantDc);
    }

    /// <summary>The DC quantization step of channel <paramref name="c"/> given the DC dequantization multipliers.</summary>
    public float DcStep(int c, ReadOnlySpan<float> dcQuant) => InverseQuantDc * dcQuant[c];
}

/// <summary>
/// The block context map: how a block's DC bucket, quantization field, transform order and channel select one of
/// the AC entropy contexts.
/// </summary>
internal sealed class BlockContextMap
{
    public const int NonZeroBuckets = 37;
    public const int ZeroDensityContextCount = 458;
    public const int ZeroDensityContextLimit = 474;

    private static readonly byte[] DefaultContextMap =
    [
        // The default clusters all the large transforms together.
        0, 1, 2, 2, 3, 3, 4, 5, 6, 6, 6, 6, 6,
        7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
        7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
    ];

    public BlockContextMap()
    {
        ContextMap = (byte[])DefaultContextMap.Clone();
        NumContexts = ContextMap.Max() + 1;
        NumDcContexts = 1;
        DcThresholds = [[], [], []];
        QfThresholds = [];
    }

    private BlockContextMap(int[][] dcThresholds, uint[] qfThresholds, byte[] contextMap, int numContexts, int numDcContexts)
    {
        DcThresholds = dcThresholds;
        QfThresholds = qfThresholds;
        ContextMap = contextMap;
        NumContexts = numContexts;
        NumDcContexts = numDcContexts;
    }

    public int[][] DcThresholds { get; }

    public uint[] QfThresholds { get; }

    public byte[] ContextMap { get; }

    public int NumContexts { get; }

    public int NumDcContexts { get; }

    /// <summary>The total number of AC entropy contexts per histogram set.</summary>
    public int NumAcContexts => NumContexts * (NonZeroBuckets + ZeroDensityContextCount);

    public static BlockContextMap Read(ref JxlBitReader br)
    {
        if (br.ReadBool())
        {
            return new BlockContextMap();
        }

        var dcThresholds = new int[3][];
        int numDcContexts = 1;
        for (int j = 0; j < 3; j++)
        {
            dcThresholds[j] = new int[br.ReadBits(4)];
            numDcContexts *= dcThresholds[j].Length + 1;
            for (int i = 0; i < dcThresholds[j].Length; i++)
            {
                uint raw = JxlFieldReader.ReadU32(ref br, U32Dist.Bits(4), U32Dist.BitsOffset(8, 16), U32Dist.BitsOffset(16, 272), U32Dist.BitsOffset(32, 65808));
                dcThresholds[j][i] = JxlFieldReader.UnpackSigned(raw);
            }
        }

        var qfThresholds = new uint[br.ReadBits(4)];
        for (int i = 0; i < qfThresholds.Length; i++)
        {
            qfThresholds[i] = JxlFieldReader.ReadU32(ref br, U32Dist.Bits(2), U32Dist.BitsOffset(3, 4), U32Dist.BitsOffset(5, 12), U32Dist.BitsOffset(8, 44)) + 1;
        }

        br.ThrowIfOverrun();
        if (numDcContexts * (qfThresholds.Length + 1) > 64)
        {
            throw new JxlDecodingException("Invalid block context map: too big.");
        }

        var contextMap = new byte[3 * AcStrategy.NumOrders * numDcContexts * (qfThresholds.Length + 1)];
        int numContexts = JxlEntropyCode.ReadContextMap(ref br, contextMap);
        if (numContexts > 16)
        {
            throw new JxlDecodingException("Invalid block context map: too many distinct contexts.");
        }

        return new BlockContextMap(dcThresholds, qfThresholds, contextMap, numContexts, numDcContexts);
    }

    /// <summary>Selects the block context of a block from its DC bucket, quant-field value, order bucket and channel.</summary>
    public int Context(int dcIndex, uint qf, int order, int channel)
    {
        int qfIndex = 0;
        foreach (uint t in QfThresholds)
        {
            if (qf > t)
            {
                qfIndex++;
            }
        }

        int idx = channel < 2 ? channel ^ 1 : 2;
        idx = (idx * AcStrategy.NumOrders) + order;
        idx = (idx * (QfThresholds.Length + 1)) + qfIndex;
        idx = (idx * NumDcContexts) + dcIndex;
        return ContextMap[idx];
    }

    /// <summary>The offset into a histogram set of the zero-density contexts of a block context.</summary>
    public int ZeroDensityContextsOffset(int blockContext) =>
        (NumContexts * NonZeroBuckets) + (ZeroDensityContextCount * blockContext);

    /// <summary>The context for the count of non-zero coefficients, from the predicted count and the block context.</summary>
    public int NonZeroContext(int nonZeros, int blockContext)
    {
        if (nonZeros >= 64)
        {
            nonZeros = 64;
        }

        int ctx = nonZeros < 8 ? nonZeros : 4 + (nonZeros / 2);
        return (ctx * NumContexts) + blockContext;
    }
}

/// <summary>Chroma-from-luma parameters: how much of the Y channel is added to X and B, per 64x64 tile plus a DC-specific amount.</summary>
internal sealed class ColorCorrelation
{
    public const int TileDim = 64;
    public const int TileDimInBlocks = TileDim / 8;
    private const uint DefaultColorFactor = 84;
    private const float DefaultBaseCorrelationB = 1.0f; // kYToBRatio

    private uint _colorFactor = DefaultColorFactor;
    private float _colorScale = 1.0f / DefaultColorFactor;
    private float _baseCorrelationX;
    private float _baseCorrelationB = DefaultBaseCorrelationB;
    private int _ytoxDc;
    private int _ytobDc;

    public ColorCorrelation()
    {
        RecomputeDcFactors();
    }

    /// <summary>Whether this map is the one a lossless JPEG recompression uses (no base correlation, no DC correlation, the default factor).</summary>
    public bool IsJpegCompatible =>
        _baseCorrelationX == 0 && _baseCorrelationB == 0 && _ytobDc == 0 && _ytoxDc == 0 && _colorFactor == DefaultColorFactor;

    /// <summary>The DC contribution factors for X (index 0) and B (index 2).</summary>
    public float[] DcFactors { get; } = new float[4];

    public float YToXRatio(int factor) => _baseCorrelationX + (factor * _colorScale);

    public float YToBRatio(int factor) => _baseCorrelationB + (factor * _colorScale);

    public void Read(ref JxlBitReader br)
    {
        if (br.ReadBool())
        {
            return;
        }

        _colorFactor = JxlFieldReader.ReadU32(ref br, U32Dist.Val(DefaultColorFactor), U32Dist.Val(256), U32Dist.BitsOffset(8, 2), U32Dist.BitsOffset(16, 258));
        _colorScale = 1.0f / _colorFactor;
        _baseCorrelationX = JxlFieldReader.ReadF16(ref br);
        if (Math.Abs(_baseCorrelationX) > 4.0f)
        {
            throw new JxlDecodingException("Base X correlation is out of range.");
        }

        _baseCorrelationB = JxlFieldReader.ReadF16(ref br);
        if (Math.Abs(_baseCorrelationB) > 4.0f)
        {
            throw new JxlDecodingException("Base B correlation is out of range.");
        }

        _ytoxDc = (int)br.ReadBits(8) + sbyte.MinValue;
        _ytobDc = (int)br.ReadBits(8) + sbyte.MinValue;
        br.ThrowIfOverrun();
        RecomputeDcFactors();
    }

    private void RecomputeDcFactors()
    {
        DcFactors[0] = YToXRatio(_ytoxDc);
        DcFactors[2] = YToBRatio(_ytobDc);
    }
}
