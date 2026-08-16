namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>AV1 block size constants (spec §3, <c>BLOCK_SIZES</c> = 22), in the spec's own enum order.</summary>
internal static class Av1BlockSize
{
    public const int Block4x4 = 0;
    public const int Block4x8 = 1;
    public const int Block8x4 = 2;
    public const int Block8x8 = 3;
    public const int Block8x16 = 4;
    public const int Block16x8 = 5;
    public const int Block16x16 = 6;
    public const int Block16x32 = 7;
    public const int Block32x16 = 8;
    public const int Block32x32 = 9;
    public const int Block32x64 = 10;
    public const int Block64x32 = 11;
    public const int Block64x64 = 12;
    public const int Block64x128 = 13;
    public const int Block128x64 = 14;
    public const int Block128x128 = 15;
    public const int Block4x16 = 16;
    public const int Block16x4 = 17;
    public const int Block8x32 = 18;
    public const int Block32x8 = 19;
    public const int Block16x64 = 20;
    public const int Block64x16 = 21;
    public const int Invalid = -1;

    public const int Count = 22;
}

/// <summary>AV1 partition type constants (spec §3, <c>PARTITION_TYPES</c>/extended set).</summary>
internal static class Av1PartitionType
{
    public const int None = 0;
    public const int Horz = 1;
    public const int Vert = 2;
    public const int Split = 3;
    public const int HorzA = 4;
    public const int HorzB = 5;
    public const int VertA = 6;
    public const int VertB = 7;
    public const int Horz4 = 8;
    public const int Vert4 = 9;
}

/// <summary>AV1 transform size constants (spec §3, <c>TX_SIZES_ALL</c> = 19), in the spec's own enum order.</summary>
internal static class Av1TxSize
{
    public const int Tx4x4 = 0;
    public const int Tx8x8 = 1;
    public const int Tx16x16 = 2;
    public const int Tx32x32 = 3;
    public const int Tx64x64 = 4;
    public const int Tx4x8 = 5;
    public const int Tx8x4 = 6;
    public const int Tx8x16 = 7;
    public const int Tx16x8 = 8;
    public const int Tx16x32 = 9;
    public const int Tx32x16 = 10;
    public const int Tx32x64 = 11;
    public const int Tx64x32 = 12;
    public const int Tx4x16 = 13;
    public const int Tx16x4 = 14;
    public const int Tx8x32 = 15;
    public const int Tx32x8 = 16;
    public const int Tx16x64 = 17;
    public const int Tx64x16 = 18;

    public const int Count = 19;
}

/// <summary>
/// AV1's block-size/transform-size conversion tables (spec §9.3 "Conversion tables"), extracted directly
/// from the specification text. Indexed by the <see cref="Av1BlockSize"/>/<see cref="Av1TxSize"/>
/// constants above, in the same order the spec defines them.
/// </summary>
internal static class Av1BlockTables
{
    public static readonly int[] MiWidthLog2 = [0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 0, 2, 1, 3, 2, 4];

    public static readonly int[] MiHeightLog2 = [0, 1, 0, 1, 2, 1, 2, 3, 2, 3, 4, 3, 4, 5, 4, 5, 2, 0, 3, 1, 4, 2];

    public static readonly int[] Num4x4BlocksWide = [1, 1, 2, 2, 2, 4, 4, 4, 8, 8, 8, 16, 16, 16, 32, 32, 1, 4, 2, 8, 4, 16];

    public static readonly int[] Num4x4BlocksHigh = [1, 2, 1, 2, 4, 2, 4, 8, 4, 8, 16, 8, 16, 32, 16, 32, 4, 1, 8, 2, 16, 4];

    public static int BlockWidth(int blockSize) => 4 * Num4x4BlocksWide[blockSize];

    public static int BlockHeight(int blockSize) => 4 * Num4x4BlocksHigh[blockSize];

    /// <summary>Maps a block size to a context group for intra syntax elements (spec §9.3).</summary>
    public static readonly int[] SizeGroup = [0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 0, 0, 1, 1, 2, 2];

    /// <summary>The largest transform size (square or rectangular) usable for a given luma block size.</summary>
    public static readonly int[] MaxTxSizeRect =
    [
        Av1TxSize.Tx4x4, Av1TxSize.Tx4x8, Av1TxSize.Tx8x4, Av1TxSize.Tx8x8,
        Av1TxSize.Tx8x16, Av1TxSize.Tx16x8, Av1TxSize.Tx16x16, Av1TxSize.Tx16x32,
        Av1TxSize.Tx32x16, Av1TxSize.Tx32x32, Av1TxSize.Tx32x64, Av1TxSize.Tx64x32,
        Av1TxSize.Tx64x64, Av1TxSize.Tx64x64, Av1TxSize.Tx64x64, Av1TxSize.Tx64x64,
        Av1TxSize.Tx4x16, Av1TxSize.Tx16x4, Av1TxSize.Tx8x32, Av1TxSize.Tx32x8,
        Av1TxSize.Tx16x64, Av1TxSize.Tx64x16,
    ];

    /// <summary>The transform size reached after one more split (spec §9.3), e.g. TX_64X64 -&gt; TX_32X32, TX_4X16 -&gt; TX_4X8.</summary>
    public static readonly int[] SplitTxSize =
    [
        Av1TxSize.Tx4x4, // TX_4X4
        Av1TxSize.Tx4x4, // TX_8X8
        Av1TxSize.Tx8x8, // TX_16X16
        Av1TxSize.Tx16x16, // TX_32X32
        Av1TxSize.Tx32x32, // TX_64X64
        Av1TxSize.Tx4x4, // TX_4X8
        Av1TxSize.Tx4x4, // TX_8X4
        Av1TxSize.Tx8x8, // TX_8X16
        Av1TxSize.Tx8x8, // TX_16X8
        Av1TxSize.Tx16x16, // TX_16X32
        Av1TxSize.Tx16x16, // TX_32X16
        Av1TxSize.Tx32x32, // TX_32X64
        Av1TxSize.Tx32x32, // TX_64X32
        Av1TxSize.Tx4x8, // TX_4X16
        Av1TxSize.Tx8x4, // TX_16X4
        Av1TxSize.Tx8x16, // TX_8X32
        Av1TxSize.Tx16x8, // TX_32X8
        Av1TxSize.Tx16x32, // TX_16X64
        Av1TxSize.Tx32x16, // TX_64X16
    ];

    /// <summary>
    /// <c>Partition_Subsize[partition][blockSize]</c> (spec §9.3): the sub-block size produced by
    /// applying <c>partition</c> to a square block of size <c>blockSize</c>. <see cref="Av1BlockSize.Invalid"/>
    /// marks combinations that never occur (rectangular block sizes, or partitions too fine for a given
    /// block size) -- the table is never accessed for those per the spec's own note.
    /// </summary>
    public static readonly int[][] PartitionSubsize =
    [
        // PARTITION_NONE
        [Av1BlockSize.Block4x4, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block8x8, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block16x16, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block32x32, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block64x64, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block128x128, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid],
        // PARTITION_HORZ
        [Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block8x4, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block16x8, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block32x16, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block64x32, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block128x64, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid],
        // PARTITION_VERT
        [Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block4x8, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block8x16, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block16x32, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block32x64, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block64x128, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid],
        // PARTITION_SPLIT
        [Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block4x4, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block8x8, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block16x16, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block32x32, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block64x64, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid],
        // PARTITION_HORZ_A
        [Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block8x4, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block16x8, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block32x16, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block64x32, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block128x64, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid],
        // PARTITION_HORZ_B
        [Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block8x4, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block16x8, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block32x16, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block64x32, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block128x64, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid],
        // PARTITION_VERT_A
        [Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block4x8, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block8x16, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block16x32, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block32x64, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block64x128, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid],
        // PARTITION_VERT_B
        [Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block4x8, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block8x16, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block16x32, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block32x64, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block64x128, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid],
        // PARTITION_HORZ_4
        [Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block16x4, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block32x8, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block64x16, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid],
        // PARTITION_VERT_4
        [Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block4x16, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block8x32, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Block16x64, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid, Av1BlockSize.Invalid],
    ];

    /// <summary>Maps a neighbor's Y intra mode to one of 5 context buckets for <c>intra_frame_y_mode</c> (spec §8.3.2).</summary>
    public static readonly int[] IntraModeContext = [0, 1, 2, 3, 4, 4, 4, 4, 3, 0, 1, 2, 0];

    /// <summary><c>Subsampled_Size[blockSize][subX][subY]</c> (spec §5.11.38): the plane-residual block size after applying chroma subsampling.</summary>
    public static readonly int[][][] SubsampledSize =
    [
        [[Av1BlockSize.Block4x4, Av1BlockSize.Block4x4], [Av1BlockSize.Block4x4, Av1BlockSize.Block4x4]],
        [[Av1BlockSize.Block4x8, Av1BlockSize.Block4x4], [Av1BlockSize.Invalid, Av1BlockSize.Block4x4]],
        [[Av1BlockSize.Block8x4, Av1BlockSize.Invalid], [Av1BlockSize.Block4x4, Av1BlockSize.Block4x4]],
        [[Av1BlockSize.Block8x8, Av1BlockSize.Block8x4], [Av1BlockSize.Block4x8, Av1BlockSize.Block4x4]],
        [[Av1BlockSize.Block8x16, Av1BlockSize.Block8x8], [Av1BlockSize.Invalid, Av1BlockSize.Block4x8]],
        [[Av1BlockSize.Block16x8, Av1BlockSize.Invalid], [Av1BlockSize.Block8x8, Av1BlockSize.Block8x4]],
        [[Av1BlockSize.Block16x16, Av1BlockSize.Block16x8], [Av1BlockSize.Block8x16, Av1BlockSize.Block8x8]],
        [[Av1BlockSize.Block16x32, Av1BlockSize.Block16x16], [Av1BlockSize.Invalid, Av1BlockSize.Block8x16]],
        [[Av1BlockSize.Block32x16, Av1BlockSize.Invalid], [Av1BlockSize.Block16x16, Av1BlockSize.Block16x8]],
        [[Av1BlockSize.Block32x32, Av1BlockSize.Block32x16], [Av1BlockSize.Block16x32, Av1BlockSize.Block16x16]],
        [[Av1BlockSize.Block32x64, Av1BlockSize.Block32x32], [Av1BlockSize.Invalid, Av1BlockSize.Block16x32]],
        [[Av1BlockSize.Block64x32, Av1BlockSize.Invalid], [Av1BlockSize.Block32x32, Av1BlockSize.Block32x16]],
        [[Av1BlockSize.Block64x64, Av1BlockSize.Block64x32], [Av1BlockSize.Block32x64, Av1BlockSize.Block32x32]],
        [[Av1BlockSize.Block64x128, Av1BlockSize.Block64x64], [Av1BlockSize.Invalid, Av1BlockSize.Block32x64]],
        [[Av1BlockSize.Block128x64, Av1BlockSize.Invalid], [Av1BlockSize.Block64x64, Av1BlockSize.Block64x32]],
        [[Av1BlockSize.Block128x128, Av1BlockSize.Block128x64], [Av1BlockSize.Block64x128, Av1BlockSize.Block64x64]],
        [[Av1BlockSize.Block4x16, Av1BlockSize.Block4x8], [Av1BlockSize.Invalid, Av1BlockSize.Block4x8]],
        [[Av1BlockSize.Block16x4, Av1BlockSize.Invalid], [Av1BlockSize.Block8x4, Av1BlockSize.Block8x4]],
        [[Av1BlockSize.Block8x32, Av1BlockSize.Block8x16], [Av1BlockSize.Invalid, Av1BlockSize.Block4x16]],
        [[Av1BlockSize.Block32x8, Av1BlockSize.Invalid], [Av1BlockSize.Block16x8, Av1BlockSize.Block16x4]],
        [[Av1BlockSize.Block16x64, Av1BlockSize.Block16x32], [Av1BlockSize.Invalid, Av1BlockSize.Block8x32]],
        [[Av1BlockSize.Block64x16, Av1BlockSize.Invalid], [Av1BlockSize.Block32x16, Av1BlockSize.Block32x8]],
    ];

    /// <summary>The plane-residual block size for <paramref name="plane"/> (0 = luma, no subsampling applied) given the coded luma block size (spec §5.11.38 <c>get_plane_residual_size</c>).</summary>
    public static int GetPlaneResidualSize(int subsize, int plane, bool subsamplingX, bool subsamplingY)
    {
        int subX = plane > 0 && subsamplingX ? 1 : 0;
        int subY = plane > 0 && subsamplingY ? 1 : 0;
        return SubsampledSize[subsize][subX][subY];
    }
}

/// <summary>AV1 intra prediction mode constants (spec §3). <c>UvCflPred</c> is chroma-only.</summary>
internal static class Av1IntraMode
{
    public const int DcPred = 0;
    public const int VPred = 1;
    public const int HPred = 2;
    public const int D45Pred = 3;
    public const int D135Pred = 4;
    public const int D113Pred = 5;
    public const int D157Pred = 6;
    public const int D203Pred = 7;
    public const int D67Pred = 8;
    public const int SmoothPred = 9;
    public const int SmoothVPred = 10;
    public const int SmoothHPred = 11;
    public const int PaethPred = 12;
    public const int UvCflPred = 13;

    public const int Count = 13;

    /// <summary>Whether <paramref name="mode"/> uses a directional (angle-adjustable) predictor -- spec §5.11.44: <c>V_PRED &lt;= mode &lt;= D67_PRED</c>.</summary>
    public static bool IsDirectional(int mode) => mode is >= VPred and <= D67Pred;
}
