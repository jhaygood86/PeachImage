using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Formats.Jxl.Encoder.VarDct;

/// <summary>The quantization choices of a VarDCT frame.</summary>
/// <param name="AcScale">The multiplier on the dequantization matrices at the frame's base quantization: smaller is finer.</param>
/// <param name="QuantDc">The DC quantization divisor: larger is finer.</param>
/// <param name="Deadzone">How far below 0.5 (in quantization steps) a magnitude may fall and still round up to the next level.</param>
/// <param name="XQmScale">The X channel's quantization-matrix scale (0-7).</param>
/// <param name="BQmScale">The B channel's quantization-matrix scale (0-7).</param>
/// <param name="Gaborish">Whether the decoder applies its Gabor-like smoothing; the encoder then sharpens the planes by its inverse first.</param>
/// <param name="EpfIterations">The number of edge-preserving-filter passes the decoder applies (0-3).</param>
/// <param name="EpfSharpness">The edge-preserving filter's per-block sharpness (0 disables it for the block, 7 is the strongest edge preservation).</param>
/// <param name="Effort">1-9: which transform sizes the encoder tries (8x8 only up to 3, adding 16-wide blocks from 4, 32-wide from 7).</param>
/// <param name="AdaptiveQuantization">How strongly the quantization follows local activity: 0 keeps one step everywhere, 1 is the full model.</param>
/// <param name="ChromaFromLuma">Whether X and B are predicted from the dequantized luma.</param>
internal readonly record struct JxlVarDctSettings(
    float AcScale,
    int QuantDc,
    float Deadzone,
    int XQmScale = 3,
    int BQmScale = 2,
    bool Gaborish = false,
    int EpfIterations = 0,
    int EpfSharpness = 4,
    int Effort = 7,
    float AdaptiveQuantization = 1f,
    bool ChromaFromLuma = true);

/// <summary>
/// Everything the group workers of one VarDCT frame share: the XYB planes, the per-block analysis, the dequantization tables
/// and the arrays they fill in for the frame-level streams (block types, quantization field, chroma-from-luma maps, quantized DC).
/// Workers write disjoint ranges, so no locking is needed.
/// </summary>
internal sealed class JxlVarDctFrameState
{
    /// <summary>The raw quantization value of an average block; the global scale is chosen so this gives the requested step.</summary>
    public const int BaseRawQuant = 64;

    /// <summary>Marks a cell covered by a larger transform that starts in another cell.</summary>
    public const byte Covered = 0xFF;

    public JxlVarDctFrameState(JxlFrameDimensions dims, float[][] planes, JxlVarDctSettings settings, DequantMatrices matrices, float invGlobalScale, float[] dcMultipliers)
    {
        Dims = dims;
        BlocksX = dims.XSizeBlocks;
        BlocksY = dims.YSizeBlocks;
        Stride = BlocksX * 8;
        Planes = planes;
        Settings = settings;
        InvGlobalScale = invGlobalScale;
        DcMultipliers = dcMultipliers;
        int blocks = BlocksX * BlocksY;
        AqMultiplier = new float[blocks];
        Array.Fill(AqMultiplier, 1f);
        CellStrategy = new byte[blocks];
        RawQuant = new int[blocks];
        TilesX = (BlocksX + ColorCorrelation.TileDimInBlocks - 1) / ColorCorrelation.TileDimInBlocks;
        TilesY = (BlocksY + ColorCorrelation.TileDimInBlocks - 1) / ColorCorrelation.TileDimInBlocks;
        YToX = new sbyte[TilesX * TilesY];
        YToB = new sbyte[TilesX * TilesY];
        QuantizedDc = [new int[blocks], new int[blocks], new int[blocks]];

        // The dequantization tables are built lazily and not thread-safe, so build every one the workers may touch up front.
        Matrices = new float[AcStrategy.Count][];
        foreach (int strategy in JxlVarDctTransform.Supported)
        {
            Matrices[strategy] = matrices.Matrix(strategy);
        }

        XChannelScale = MathF.Pow(1f / 1.25f, settings.XQmScale - 2f);
        BChannelScale = MathF.Pow(1f / 1.25f, settings.BQmScale - 2f);
        Biases = JxlVarDctConstants.QuantBiases;
        BlockContext = new BlockContextMap();
    }

    public JxlFrameDimensions Dims { get; }

    public int BlocksX { get; }

    public int BlocksY { get; }

    /// <summary>The padded row stride of <see cref="Planes"/> in samples.</summary>
    public int Stride { get; }

    public float[][] Planes { get; }

    public JxlVarDctSettings Settings { get; }

    public float InvGlobalScale { get; }

    /// <summary>The DC dequantization step of X, Y and B.</summary>
    public float[] DcMultipliers { get; }

    /// <summary>The ratio of Y added to X (index 0) and to B (index 2) when reconstructing DC.</summary>
    public float[] DcCorrelation { get; } = [0f, 0f, 1f];

    /// <summary>The DC correlation of X as a multiple of <c>1 / ColorFactor</c> above its base of zero.</summary>
    public int DcFactorX { get; set; }

    /// <summary>The DC correlation of B as a multiple of <c>1 / ColorFactor</c> above its base of one.</summary>
    public int DcFactorB { get; set; }

    public float XChannelScale { get; }

    public float BChannelScale { get; }

    public float[] Biases { get; }

    public float[][] Matrices { get; }

    public BlockContextMap BlockContext { get; }

    /// <summary>Per 8x8 block: how much finer than average to quantize it (more where the content is smooth).</summary>
    public float[] AqMultiplier { get; }

    /// <summary>Per cell: the strategy of the transform starting there, or <see cref="Covered"/>.</summary>
    public byte[] CellStrategy { get; }

    /// <summary>Per transform's first cell: its raw quantization value (1-256).</summary>
    public int[] RawQuant { get; }

    public int TilesX { get; }

    public int TilesY { get; }

    public sbyte[] YToX { get; }

    public sbyte[] YToB { get; }

    /// <summary>Per cell, in X, Y, B order: the quantized DC (or, for a large transform, the DC image value standing in for its lowest frequencies).</summary>
    public int[][] QuantizedDc { get; }
}

/// <summary>The decoder's default quantization-bias and colour-correlation constants the encoder mirrors.</summary>
internal static class JxlVarDctConstants
{
    public static float[] QuantBiases { get; } = PeachImage.Formats.Jxl.Headers.JxlCustomTransformData.DefaultQuantBiases.ToArray();

    /// <summary>The colour factor the default correlation maps use: a correlation of one step is <c>1 / 84</c>.</summary>
    public const int ColorFactor = 84;
}
