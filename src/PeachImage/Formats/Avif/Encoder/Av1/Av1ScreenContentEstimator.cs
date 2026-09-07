namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Faithful port of libaom's <c>estimate_screen_content</c> (<c>av1/encoder/encoder.c</c>) -- the static,
/// per-16x16-luma-block color-count/variance heuristic real encoders use to decide, per still-picture frame,
/// whether palette mode (<c>allow_screen_content_tools</c>) and IntraBC (<c>allow_intrabc</c>) are worth
/// signaling at all. This is a genuine gap this project's own harness (project plan
/// "compare-avif-encoding-to-lucky-clover.md", Phase 0-2) measured directly: PeachImage previously tied both
/// flags unconditionally to <c>lossless</c>, producing correct-but-needlessly-costly output on photographic
/// content (a few wasted header/per-leaf bits) and, worse, producing a badly *undersized* estimate of when
/// screen-content tooling actually helps versus real encoder behavior on some content -- but the far larger,
/// separately-tracked gap is that even when these flags are correctly enabled, this project's IntraBC/palette
/// *search* itself is still a bounded approximation of libaom's own (see the project plan's Phase 2). This
/// class only fixes the frame-level on/off decision, not the search quality once enabled.
/// </summary>
internal static class Av1ScreenContentEstimator
{
    private const int BlockSize = 16;
    private const int BlockArea = BlockSize * BlockSize;
    private const int ColorCountThreshold = 4;

    /// <summary>
    /// Divides <paramref name="trueWidth"/>x<paramref name="trueHeight"/> of <paramref name="lumaY"/> (stride
    /// <paramref name="planeWidth"/>) into non-overlapping 16x16 blocks (a partial trailing row/column is
    /// simply not sampled, matching libaom's own <c>r + kBlockHeight &lt;= height</c> loop bound), and scores
    /// each block's distinct 8-bit luma value count and per-pixel population variance -- exactly libaom's own
    /// two thresholds (<c>kColorThresh = 4</c>, <c>kVarThresh = 0</c>) and final area-ratio decision
    /// (<c>counts_1 * kBlockArea * 10 &gt; area</c> for palette, <c>counts_2 * kBlockArea * 12 &gt; area</c>
    /// additionally for IntraBC, both scaled by 10/100 and 12/100 respectively -- these are libaom's own
    /// experimentally-chosen constants, not independently re-derived).
    /// </summary>
    public static (bool AllowScreenContentTools, bool AllowIntrabc) Estimate(int[] lumaY, int planeWidth, int trueWidth, int trueHeight)
    {
        long area = (long)trueWidth * trueHeight;
        if (area == 0)
        {
            return (false, false);
        }

        long counts1 = 0;
        long counts2 = 0;
        Span<int> valueCounts = stackalloc int[256];

        for (int r = 0; r + BlockSize <= trueHeight; r += BlockSize)
        {
            for (int c = 0; c + BlockSize <= trueWidth; c += BlockSize)
            {
                int numColors = CountColors(lumaY, planeWidth, r, c, valueCounts);
                if (numColors > 1 && numColors <= ColorCountThreshold)
                {
                    counts1++;
                    if (ComputeVariance(lumaY, planeWidth, r, c) > 0)
                    {
                        counts2++;
                    }
                }
            }
        }

        bool allowScreenContentTools = (counts1 * BlockArea * 10) > area;
        bool allowIntrabc = allowScreenContentTools && (counts2 * BlockArea * 12) > area;
        return (allowScreenContentTools, allowIntrabc);
    }

    private static int CountColors(int[] lumaY, int planeWidth, int blockRow, int blockCol, Span<int> valueCounts)
    {
        valueCounts.Clear();
        for (int i = 0; i < BlockSize; i++)
        {
            int rowBase = ((blockRow + i) * planeWidth) + blockCol;
            for (int j = 0; j < BlockSize; j++)
            {
                valueCounts[lumaY[rowBase + j]]++;
            }
        }

        int numColors = 0;
        for (int i = 0; i < valueCounts.Length; i++)
        {
            if (valueCounts[i] != 0)
            {
                numColors++;
            }
        }

        return numColors;
    }

    /// <summary>libaom's <c>av1_get_perpixel_variance</c>: population variance of the block's raw luma values (no reference/prediction subtracted), rounded to the nearest integer -- matches <c>ROUND_POWER_OF_TWO(sse - sum*sum/N, log2(N))</c> exactly.</summary>
    private static long ComputeVariance(int[] lumaY, int planeWidth, int blockRow, int blockCol)
    {
        long sum = 0;
        long sumSq = 0;
        for (int i = 0; i < BlockSize; i++)
        {
            int rowBase = ((blockRow + i) * planeWidth) + blockCol;
            for (int j = 0; j < BlockSize; j++)
            {
                int value = lumaY[rowBase + j];
                sum += value;
                sumSq += (long)value * value;
            }
        }

        long variance = sumSq - ((sum * sum) / BlockArea);
        return (variance + (BlockArea / 2)) / BlockArea;
    }
}
