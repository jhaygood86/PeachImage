namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Faithful port of two small, pure real libaom helper functions -- <c>av1_find_dominant_value</c>/
/// <c>av1_dilate_block</c> (<c>av1/encoder/encoder.c</c>) -- used by libaom's own real "screen content
/// detection mode 2" (<c>estimate_screen_content_antialiasing_aware</c>, selectable via
/// <c>--screen-detection-mode</c>): dilating a block's dominant value outward by one pixel in all 8
/// directions before counting unique colors, so a thin band of anti-aliased edge pixels around a large flat
/// region doesn't get miscounted as a lot of extra distinct colors.
///
/// <para>Not yet wired into any real per-frame decision: <see cref="Av1ScreenContentEstimator"/> ports
/// libaom's original, simpler screen-content heuristic ("mode 1") -- mode 2's own full antialiasing-aware
/// classifier (16x16-block color-count/variance cascade around these two primitives) is separate, future,
/// more substantial work. This class ports the two small, pure, independently-testable primitives libaom's
/// own real <c>screen_content_detection_mode_2_test.cc</c> covers, verified correct and ready for that
/// future work to call.</para>
/// </summary>
internal static class Av1ScreenContentDilation
{
    /// <summary>
    /// <c>av1_find_dominant_value</c>: a 256-bin histogram over an 8-bit <paramref name="src"/> block,
    /// returning the most frequent value (first value reached wins a tie, matching libaom's own real
    /// strict <c>&gt;</c> comparison).
    /// </summary>
    public static byte FindDominantValue(ReadOnlySpan<byte> src, int stride, int rows, int cols)
    {
        Span<uint> valueCount = stackalloc uint[256];
        uint dominantValueCount = 0;
        byte dominantValue = 0;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                byte value = src[(r * stride) + c];
                valueCount[value]++;

                if (valueCount[value] > dominantValueCount)
                {
                    dominantValue = value;
                    dominantValueCount = valueCount[value];
                }
            }
        }

        return dominantValue;
    }

    /// <summary>
    /// <c>av1_dilate_block</c>: copies <paramref name="src"/> into <paramref name="dilated"/>, then extends
    /// every occurrence of the block's own dominant value (<see cref="FindDominantValue"/>) one pixel
    /// outward in all 8 directions (4 sides + 4 corners), clamped at the block's own edges.
    /// </summary>
    public static void DilateBlock(ReadOnlySpan<byte> src, int srcStride, Span<byte> dilated, int dilatedStride, int rows, int cols)
    {
        byte dominantValue = FindDominantValue(src, srcStride, rows, cols);

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                dilated[(r * dilatedStride) + c] = src[(r * srcStride) + c];
            }
        }

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                byte value = src[(r * srcStride) + c];
                if (value != dominantValue)
                {
                    continue;
                }

                if (r != 0)
                {
                    dilated[((r - 1) * dilatedStride) + c] = value;
                }

                if (r != rows - 1)
                {
                    dilated[((r + 1) * dilatedStride) + c] = value;
                }

                if (c != 0)
                {
                    dilated[(r * dilatedStride) + c - 1] = value;
                }

                if (c != cols - 1)
                {
                    dilated[(r * dilatedStride) + c + 1] = value;
                }

                if (r != 0 && c != 0)
                {
                    dilated[((r - 1) * dilatedStride) + c - 1] = value;
                }

                if (r != 0 && c != cols - 1)
                {
                    dilated[((r - 1) * dilatedStride) + c + 1] = value;
                }

                if (r != rows - 1 && c != 0)
                {
                    dilated[((r + 1) * dilatedStride) + c - 1] = value;
                }

                if (r != rows - 1 && c != cols - 1)
                {
                    dilated[((r + 1) * dilatedStride) + c + 1] = value;
                }
            }
        }
    }
}
