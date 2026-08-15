namespace PeachImage.Formats.Webp.Decoding.Vp8.LoopFilter;

/// <summary>
/// VP8's "simple" in-loop deblocking filter (RFC 6386 section 15.2): luma-only, gated by
/// <see cref="Vp8LoopFilterThresholds.NeedsFilter"/> and applied with the 2-pixel-in/2-pixel-out
/// <see cref="Vp8LoopFilterThresholds.DoFilter2"/>. Matches libwebp's <c>src/dsp/dec.c</c>
/// <c>SimpleVFilter16_C</c>/<c>SimpleHFilter16_C</c> (macroblock boundaries) and
/// <c>SimpleVFilter16i_C</c>/<c>SimpleHFilter16i_C</c> (the three interior subblock edges).
/// </summary>
internal static class Vp8SimpleLoopFilter
{
    /// <summary>Filters the horizontal boundary at <paramref name="origin"/> (the top macroblock edge, or one of its 3 interior counterparts), scanning across all 16 columns.</summary>
    public static void FilterTopEdge(Span<byte> plane, int origin, int stride, int thresh) => FilterEdge(plane, origin, stride, 1, thresh);

    /// <summary>Filters the vertical boundary at <paramref name="origin"/> (the left macroblock edge, or one of its 3 interior counterparts), scanning across all 16 rows.</summary>
    public static void FilterLeftEdge(Span<byte> plane, int origin, int stride, int thresh) => FilterEdge(plane, origin, 1, stride, thresh);

    public static void FilterTopEdgesInner(Span<byte> plane, int origin, int stride, int thresh)
    {
        for (int k = 1; k <= 3; k++)
        {
            FilterTopEdge(plane, origin + (4 * k * stride), stride, thresh);
        }
    }

    public static void FilterLeftEdgesInner(Span<byte> plane, int origin, int stride, int thresh)
    {
        for (int k = 1; k <= 3; k++)
        {
            FilterLeftEdge(plane, origin + (4 * k), stride, thresh);
        }
    }

    private static void FilterEdge(Span<byte> plane, int origin, int acrossStep, int alongStep, int thresh)
    {
        int thresh2 = (2 * thresh) + 1;
        for (int i = 0; i < 16; i++)
        {
            int pos = origin + (i * alongStep);
            if (Vp8LoopFilterThresholds.NeedsFilter(plane, pos, acrossStep, thresh2))
            {
                Vp8LoopFilterThresholds.DoFilter2(plane, pos, acrossStep);
            }
        }
    }
}