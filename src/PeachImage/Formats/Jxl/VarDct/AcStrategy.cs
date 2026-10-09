using System.Numerics;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>The transform used for a block of a VarDCT frame (JPEG XL's "AC strategies").</summary>
internal enum AcStrategyType : byte
{
    Dct = 0,
    Identity = 1,
    Dct2x2 = 2,
    Dct4x4 = 3,
    Dct16x16 = 4,
    Dct32x32 = 5,
    Dct16x8 = 6,
    Dct8x16 = 7,
    Dct32x8 = 8,
    Dct8x32 = 9,
    Dct32x16 = 10,
    Dct16x32 = 11,
    Dct4x8 = 12,
    Dct8x4 = 13,
    Afv0 = 14,
    Afv1 = 15,
    Afv2 = 16,
    Afv3 = 17,
    Dct64x64 = 18,
    Dct64x32 = 19,
    Dct32x64 = 20,
    Dct128x128 = 21,
    Dct128x64 = 22,
    Dct64x128 = 23,
    Dct256x256 = 24,
    Dct256x128 = 25,
    Dct128x256 = 26,
}

/// <summary>Static facts about each <see cref="AcStrategyType"/>: how many 8x8 blocks it covers and its coefficient scan order.</summary>
internal static class AcStrategy
{
    public const int Count = 27;
    public const int BlockDim = 8;
    public const int BlockSize = 64;
    public const int NumOrders = 13;

    private static readonly byte[] CoveredX = [1, 1, 1, 1, 2, 4, 1, 2, 1, 4, 2, 4, 1, 1, 1, 1, 1, 1, 8, 4, 8, 16, 8, 16, 32, 16, 32];

    private static readonly byte[] CoveredY = [1, 1, 1, 1, 2, 4, 2, 1, 4, 1, 4, 2, 1, 1, 1, 1, 1, 1, 8, 8, 4, 16, 16, 8, 32, 32, 16];

    private static readonly byte[] Log2Covered = [0, 0, 0, 0, 2, 4, 1, 1, 2, 2, 3, 3, 0, 0, 0, 0, 0, 0, 6, 5, 5, 8, 7, 7, 10, 9, 9];

    /// <summary>Strategies with different natural orders occupy different buckets here; transposes share one.</summary>
    private static readonly byte[] OrderBucket = [0, 1, 1, 1, 2, 3, 4, 4, 5, 5, 6, 6, 1, 1, 1, 1, 1, 1, 7, 8, 8, 9, 10, 10, 11, 12, 12];

    private static readonly int[]?[] NaturalOrders = new int[]?[Count];

    public static int CoveredBlocksX(int strategy) => CoveredX[strategy];

    public static int CoveredBlocksY(int strategy) => CoveredY[strategy];

    public static int Log2CoveredBlocks(int strategy) => Log2Covered[strategy];

    public static int OrderOf(int strategy) => OrderBucket[strategy];

    public static bool IsValid(int rawStrategy) => (uint)rawStrategy < Count;

    /// <summary>
    /// Returns the natural scan order of a strategy: element <c>i</c> is the position (in the block's coefficient layout, whose
    /// rows are the smaller dimension) of the i-th coefficient. A generalization of the zigzag order to non-square blocks.
    /// </summary>
    public static int[] NaturalOrder(int strategy)
    {
        var cached = NaturalOrders[strategy];
        if (cached is not null)
        {
            return cached;
        }

        // CoefficientLayout: the number of rows of coefficients is always the smaller coordinate.
        int cx = Math.Max(CoveredX[strategy], CoveredY[strategy]);
        int cy = Math.Min(CoveredX[strategy], CoveredY[strategy]);
        var order = new int[cx * cy * BlockSize];

        // cx >= cy: compute the zigzag for a cx x cx block, then discard lines that are not multiples of the ratio.
        int xs = cx / cy;
        int xsm = xs - 1;
        int xss = xs <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(xs - 1));

        // First half of the block.
        int cur = cx * cy;
        for (int i = 0; i < cx * BlockDim; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                int x = j;
                int y = i - j;
                if ((i & 1) != 0)
                {
                    (x, y) = (y, x);
                }

                if ((y & xsm) != 0)
                {
                    continue;
                }

                y >>= xss;
                int val = x < cx && y < cy ? (y * cx) + x : cur++;
                order[val] = (y * cx * BlockDim) + x;
            }
        }

        // Second half.
        for (int ip = (cx * BlockDim) - 1; ip > 0; ip--)
        {
            int i = ip - 1;
            for (int j = 0; j <= i; j++)
            {
                int x = (cx * BlockDim) - 1 - (i - j);
                int y = (cx * BlockDim) - 1 - j;
                if ((i & 1) != 0)
                {
                    (x, y) = (y, x);
                }

                if ((y & xsm) != 0)
                {
                    continue;
                }

                y >>= xss;
                int val = cur++;
                order[val] = (y * cx * BlockDim) + x;
            }
        }

        NaturalOrders[strategy] = order;
        return order;
    }
}
