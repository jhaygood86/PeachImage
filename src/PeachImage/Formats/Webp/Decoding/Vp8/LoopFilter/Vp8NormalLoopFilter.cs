namespace PeachImage.Formats.Webp.Decoding.Vp8.LoopFilter;

/// <summary>
/// VP8's "normal" in-loop deblocking filter (RFC 6386 section 15.3): applies to both luma (16-wide edges) and
/// chroma (8-wide edges), gated by <see cref="Vp8LoopFilterThresholds.NeedsFilter2"/> and choosing between the
/// 2-tap (<see cref="Vp8LoopFilterThresholds.DoFilter2"/>, when <see cref="Vp8LoopFilterThresholds.Hev"/> fires)
/// and the wider 6-tap (macroblock boundary) or 4-tap (interior subblock edge) filters otherwise. Matches
/// libwebp's <c>src/dsp/dec.c</c> <c>DoFilter4_C</c>/<c>DoFilter6_C</c> and the
/// <c>VFilter16/HFilter16/VFilter8/HFilter8</c> (+ their <c>...i</c> interior-edge counterparts) driving loops.
/// </summary>
internal static class Vp8NormalLoopFilter
{
    public static void FilterTopEdge16(Span<byte> plane, int origin, int stride, int thresh, int interiorLimit, int hevThresh) =>
        FilterMacroblockEdge(plane, origin, stride, 1, 16, thresh, interiorLimit, hevThresh);

    public static void FilterLeftEdge16(Span<byte> plane, int origin, int stride, int thresh, int interiorLimit, int hevThresh) =>
        FilterMacroblockEdge(plane, origin, 1, stride, 16, thresh, interiorLimit, hevThresh);

    public static void FilterTopEdgesInner16(Span<byte> plane, int origin, int stride, int thresh, int interiorLimit, int hevThresh)
    {
        for (int k = 1; k <= 3; k++)
        {
            FilterTopEdgeInner16(plane, origin + (4 * k * stride), stride, thresh, interiorLimit, hevThresh);
        }
    }

    /// <summary>Filters a single 16-wide interior subblock edge. Split out of <see cref="FilterTopEdgesInner16"/> so one edge can be exercised in isolation.</summary>
    public static void FilterTopEdgeInner16(Span<byte> plane, int origin, int stride, int thresh, int interiorLimit, int hevThresh) =>
        FilterInnerEdge(plane, origin, stride, 1, 16, thresh, interiorLimit, hevThresh);

    public static void FilterLeftEdgesInner16(Span<byte> plane, int origin, int stride, int thresh, int interiorLimit, int hevThresh)
    {
        for (int k = 1; k <= 3; k++)
        {
            FilterLeftEdgeInner16(plane, origin + (4 * k), stride, thresh, interiorLimit, hevThresh);
        }
    }

    /// <summary>Filters a single 16-tall interior subblock edge. Split out of <see cref="FilterLeftEdgesInner16"/> so one edge can be exercised in isolation, mirroring <see cref="FilterTopEdgeInner16"/>.</summary>
    public static void FilterLeftEdgeInner16(Span<byte> plane, int origin, int stride, int thresh, int interiorLimit, int hevThresh) =>
        FilterInnerEdge(plane, origin, 1, stride, 16, thresh, interiorLimit, hevThresh);

    public static void FilterTopEdge8(Span<byte> plane, int origin, int stride, int thresh, int interiorLimit, int hevThresh) =>
        FilterMacroblockEdge(plane, origin, stride, 1, 8, thresh, interiorLimit, hevThresh);

    public static void FilterLeftEdge8(Span<byte> plane, int origin, int stride, int thresh, int interiorLimit, int hevThresh) =>
        FilterMacroblockEdge(plane, origin, 1, stride, 8, thresh, interiorLimit, hevThresh);

    public static void FilterTopEdgeInner8(Span<byte> plane, int origin, int stride, int thresh, int interiorLimit, int hevThresh) =>
        FilterInnerEdge(plane, origin + (4 * stride), stride, 1, 8, thresh, interiorLimit, hevThresh);

    public static void FilterLeftEdgeInner8(Span<byte> plane, int origin, int stride, int thresh, int interiorLimit, int hevThresh) =>
        FilterInnerEdge(plane, origin + 4, 1, stride, 8, thresh, interiorLimit, hevThresh);

    private static void FilterMacroblockEdge(Span<byte> plane, int origin, int acrossStep, int alongStep, int size, int thresh, int interiorLimit, int hevThresh)
    {
        // alongStep == 1 means the lanes are adjacent bytes (a horizontal edge); otherwise they are a stride
        // apart (a vertical edge) and need a transpose. Vp8VectorLoopFilter handles both.
        if (alongStep == 1)
        {
            if (Vp8VectorLoopFilter.CanFilter(size, origin, acrossStep, plane.Length))
            {
                Vp8VectorLoopFilter.FilterContiguousEdge(plane, origin, acrossStep, thresh, interiorLimit, hevThresh, macroblockEdge: true);
                return;
            }
        }
        else if (acrossStep == 1 && Vp8VectorLoopFilter.CanFilterStrided(size, origin, alongStep, plane.Length))
        {
            Vp8VectorLoopFilter.FilterStridedEdge(plane, origin, alongStep, thresh, interiorLimit, hevThresh, macroblockEdge: true);
            return;
        }

        int thresh2 = (2 * thresh) + 1;
        for (int i = 0; i < size; i++)
        {
            int pos = origin + (i * alongStep);
            if (Vp8LoopFilterThresholds.NeedsFilter2(plane, pos, acrossStep, thresh2, interiorLimit))
            {
                if (Vp8LoopFilterThresholds.Hev(plane, pos, acrossStep, hevThresh))
                {
                    Vp8LoopFilterThresholds.DoFilter2(plane, pos, acrossStep);
                }
                else
                {
                    DoFilter6(plane, pos, acrossStep);
                }
            }
        }
    }

    private static void FilterInnerEdge(Span<byte> plane, int origin, int acrossStep, int alongStep, int size, int thresh, int interiorLimit, int hevThresh)
    {
        if (alongStep == 1)
        {
            if (Vp8VectorLoopFilter.CanFilter(size, origin, acrossStep, plane.Length))
            {
                Vp8VectorLoopFilter.FilterContiguousEdge(plane, origin, acrossStep, thresh, interiorLimit, hevThresh, macroblockEdge: false);
                return;
            }
        }
        else if (acrossStep == 1 && Vp8VectorLoopFilter.CanFilterStrided(size, origin, alongStep, plane.Length))
        {
            Vp8VectorLoopFilter.FilterStridedEdge(plane, origin, alongStep, thresh, interiorLimit, hevThresh, macroblockEdge: false);
            return;
        }

        int thresh2 = (2 * thresh) + 1;
        for (int i = 0; i < size; i++)
        {
            int pos = origin + (i * alongStep);
            if (Vp8LoopFilterThresholds.NeedsFilter2(plane, pos, acrossStep, thresh2, interiorLimit))
            {
                if (Vp8LoopFilterThresholds.Hev(plane, pos, acrossStep, hevThresh))
                {
                    Vp8LoopFilterThresholds.DoFilter2(plane, pos, acrossStep);
                }
                else
                {
                    DoFilter4(plane, pos, acrossStep);
                }
            }
        }
    }

    /// <summary>4-pixels-in/4-pixels-out edge filter, used for interior subblock edges (libwebp's <c>DoFilter4_C</c>).</summary>
    private static void DoFilter4(Span<byte> p, int pos, int step)
    {
        int p1 = p[pos - (2 * step)];
        int p0 = p[pos - step];
        int q0 = p[pos];
        int q1 = p[pos + step];

        int a = 3 * (q0 - p0);
        int a1 = Vp8LoopFilterThresholds.SClip2((a + 4) >> 3);
        int a2 = Vp8LoopFilterThresholds.SClip2((a + 3) >> 3);
        int a3 = (a1 + 1) >> 1;

        p[pos - (2 * step)] = Vp8LoopFilterThresholds.Clip1(p1 + a3);
        p[pos - step] = Vp8LoopFilterThresholds.Clip1(p0 + a2);
        p[pos] = Vp8LoopFilterThresholds.Clip1(q0 - a1);
        p[pos + step] = Vp8LoopFilterThresholds.Clip1(q1 - a3);
    }

    /// <summary>6-pixels-in/6-pixels-out edge filter, used for macroblock boundary edges (libwebp's <c>DoFilter6_C</c>).</summary>
    private static void DoFilter6(Span<byte> p, int pos, int step)
    {
        int p2 = p[pos - (3 * step)];
        int p1 = p[pos - (2 * step)];
        int p0 = p[pos - step];
        int q0 = p[pos];
        int q1 = p[pos + step];
        int q2 = p[pos + (2 * step)];

        int a = Vp8LoopFilterThresholds.SClip1((3 * (q0 - p0)) + Vp8LoopFilterThresholds.SClip1(p1 - q1));
        int a1 = ((27 * a) + 63) >> 7;
        int a2 = ((18 * a) + 63) >> 7;
        int a3 = ((9 * a) + 63) >> 7;

        p[pos - (3 * step)] = Vp8LoopFilterThresholds.Clip1(p2 + a3);
        p[pos - (2 * step)] = Vp8LoopFilterThresholds.Clip1(p1 + a2);
        p[pos - step] = Vp8LoopFilterThresholds.Clip1(p0 + a1);
        p[pos] = Vp8LoopFilterThresholds.Clip1(q0 - a1);
        p[pos + step] = Vp8LoopFilterThresholds.Clip1(q1 - a2);
        p[pos + (2 * step)] = Vp8LoopFilterThresholds.Clip1(q2 - a3);
    }
}