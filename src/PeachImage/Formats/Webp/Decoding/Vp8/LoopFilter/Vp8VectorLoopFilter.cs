using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Decoding.Vp8.LoopFilter;

/// <summary>
/// Vectorized form of <see cref="Vp8NormalLoopFilter"/>'s per-lane work, for the edges whose lanes are
/// contiguous in memory.
/// </summary>
/// <remarks>
/// <para>
/// The deblocking filter was measured at 32% of lossy decode — the single largest bucket, ahead of entropy
/// decode. It splits roughly evenly between the two edge orientations. A "top" edge (a horizontal edge, filtered
/// vertically) walks its 16 or 8 lanes with <c>alongStep == 1</c>, so the lanes are adjacent bytes and each
/// lane's eight taps sit at fixed row offsets: eight plain vector loads, no gathering. A "left" edge is the
/// transpose of that — its lanes are a row-stride apart — and vectorizing it needs a 16x8 transpose in and out,
/// so it is left scalar here; only the contiguous orientation is handled.
/// </para>
/// <para>
/// All arithmetic is done widened to 16-bit lanes, in two halves per 16-byte edge, rather than in the saturating
/// 8-bit signed form libwebp's SSE2 path uses. That form is a genuine equivalence — saturation only ever kicks in
/// on values the subsequent clamp would have flattened anyway — but it is an equivalence that has to be argued,
/// whereas widening evaluates the scalar expressions from <see cref="Vp8NormalLoopFilter"/> and
/// <see cref="Vp8LoopFilterThresholds"/> literally, term for term. Two half-width passes still beat sixteen
/// scalar lanes comfortably, and the edge gate genuinely needs the width regardless: the filter limit reaches
/// 189, so <c>4*|p0-q0| + |p1-q1| &lt;= 2*limit+1</c> does not fit in a byte.
/// </para>
/// </remarks>
internal static class Vp8VectorLoopFilter
{
    /// <summary>Whether <see cref="FilterContiguousEdge"/> can handle an edge with these dimensions and this hardware.</summary>
    public static bool CanFilter(int size, int origin, int stride, int planeLength) =>
        Vector128.IsHardwareAccelerated
        && size == Vector128<byte>.Count
        && origin - (4 * stride) >= 0
        && origin + (3 * stride) + Vector128<byte>.Count <= planeLength;

    /// <summary>
    /// Filters one horizontal edge whose <see cref="Vector128{T}.Count"/> lanes start at
    /// <paramref name="origin"/> and run contiguously, reading and writing the rows at offsets -4..+3
    /// <paramref name="stride"/> around it.
    /// </summary>
    /// <remarks>
    /// <paramref name="macroblockEdge"/> selects the 6-tap macroblock-boundary filter over the 4-tap interior
    /// one; both fall back to the 2-tap filter wherever the high-edge-variance test fires, exactly as the
    /// scalar path does. <paramref name="thresh"/>, <paramref name="interiorLimit"/> and
    /// <paramref name="hevThreshold"/> are the same values the scalar path takes.
    /// </remarks>
    public static void FilterContiguousEdge(
        Span<byte> plane,
        int origin,
        int stride,
        int thresh,
        int interiorLimit,
        int hevThreshold,
        bool macroblockEdge)
    {
        var p3 = Load(plane, origin - (4 * stride));
        var p2 = Load(plane, origin - (3 * stride));
        var p1 = Load(plane, origin - (2 * stride));
        var p0 = Load(plane, origin - stride);
        var q0 = Load(plane, origin);
        var q1 = Load(plane, origin + stride);
        var q2 = Load(plane, origin + (2 * stride));
        var q3 = Load(plane, origin + (3 * stride));

        var thresh2 = Vector128.Create((short)((2 * thresh) + 1));
        var it = Vector128.Create((short)interiorLimit);
        var hev = Vector128.Create((short)hevThreshold);

        // Lower and upper halves of the 16 lanes, widened to 16-bit and filtered independently.
        var (p2Lo, p1Lo, p0Lo, q0Lo, q1Lo, q2Lo) = FilterHalf(
            Lower(p3), Lower(p2), Lower(p1), Lower(p0), Lower(q0), Lower(q1), Lower(q2), Lower(q3),
            thresh2, it, hev, macroblockEdge);

        var (p2Hi, p1Hi, p0Hi, q0Hi, q1Hi, q2Hi) = FilterHalf(
            Upper(p3), Upper(p2), Upper(p1), Upper(p0), Upper(q0), Upper(q1), Upper(q2), Upper(q3),
            thresh2, it, hev, macroblockEdge);

        if (macroblockEdge)
        {
            Store(plane, origin - (3 * stride), p2Lo, p2Hi);
            Store(plane, origin + (2 * stride), q2Lo, q2Hi);
        }

        Store(plane, origin - (2 * stride), p1Lo, p1Hi);
        Store(plane, origin - stride, p0Lo, p0Hi);
        Store(plane, origin, q0Lo, q0Hi);
        Store(plane, origin + stride, q1Lo, q1Hi);
    }

    /// <summary>Filters eight lanes, returning the six taps the widest filter can modify (the caller stores only the ones its filter actually writes).</summary>
    private static (Vector128<short> P2, Vector128<short> P1, Vector128<short> P0, Vector128<short> Q0, Vector128<short> Q1, Vector128<short> Q2) FilterHalf(
        Vector128<short> p3, Vector128<short> p2, Vector128<short> p1, Vector128<short> p0,
        Vector128<short> q0, Vector128<short> q1, Vector128<short> q2, Vector128<short> q3,
        Vector128<short> thresh2, Vector128<short> it, Vector128<short> hevThreshold, bool macroblockEdge)
    {
        // NeedsFilter2: the 4*|p0-q0| + |p1-q1| edge-strength gate, then every neighbouring-pair difference
        // within the interior limit.
        var edgeStrength = (Vector128.Create((short)4) * AbsDiff(p0, q0)) + AbsDiff(p1, q1);
        var mask = Vector128.LessThanOrEqual(edgeStrength, thresh2)
            & Vector128.LessThanOrEqual(AbsDiff(p3, p2), it)
            & Vector128.LessThanOrEqual(AbsDiff(p2, p1), it)
            & Vector128.LessThanOrEqual(AbsDiff(p1, p0), it)
            & Vector128.LessThanOrEqual(AbsDiff(q3, q2), it)
            & Vector128.LessThanOrEqual(AbsDiff(q2, q1), it)
            & Vector128.LessThanOrEqual(AbsDiff(q1, q0), it);

        var hev = Vector128.GreaterThan(AbsDiff(p1, p0), hevThreshold)
            | Vector128.GreaterThan(AbsDiff(q1, q0), hevThreshold);

        var useTwoTap = mask & hev;
        var useWideTap = mask & ~hev;

        var (twoTapP0, twoTapQ0) = DoFilter2(p1, p0, q0, q1);

        Vector128<short> newP2 = p2, newP1 = p1, newP0 = p0, newQ0 = q0, newQ1 = q1, newQ2 = q2;

        if (macroblockEdge)
        {
            var (wideP2, wideP1, wideP0, wideQ0, wideQ1, wideQ2) = DoFilter6(p2, p1, p0, q0, q1, q2);
            newP2 = Vector128.ConditionalSelect(useWideTap, wideP2, newP2);
            newP1 = Vector128.ConditionalSelect(useWideTap, wideP1, newP1);
            newP0 = Vector128.ConditionalSelect(useWideTap, wideP0, newP0);
            newQ0 = Vector128.ConditionalSelect(useWideTap, wideQ0, newQ0);
            newQ1 = Vector128.ConditionalSelect(useWideTap, wideQ1, newQ1);
            newQ2 = Vector128.ConditionalSelect(useWideTap, wideQ2, newQ2);
        }
        else
        {
            var (fourP1, fourP0, fourQ0, fourQ1) = DoFilter4(p1, p0, q0, q1);
            newP1 = Vector128.ConditionalSelect(useWideTap, fourP1, newP1);
            newP0 = Vector128.ConditionalSelect(useWideTap, fourP0, newP0);
            newQ0 = Vector128.ConditionalSelect(useWideTap, fourQ0, newQ0);
            newQ1 = Vector128.ConditionalSelect(useWideTap, fourQ1, newQ1);
        }

        newP0 = Vector128.ConditionalSelect(useTwoTap, twoTapP0, newP0);
        newQ0 = Vector128.ConditionalSelect(useTwoTap, twoTapQ0, newQ0);

        return (newP2, newP1, newP0, newQ0, newQ1, newQ2);
    }

    /// <summary>2-pixels-in/2-pixels-out edge filter — <see cref="Vp8LoopFilterThresholds.DoFilter2"/>, lane for lane.</summary>
    private static (Vector128<short> P0, Vector128<short> Q0) DoFilter2(
        Vector128<short> p1, Vector128<short> p0, Vector128<short> q0, Vector128<short> q1)
    {
        var a = (Vector128.Create((short)3) * (q0 - p0)) + SClip1(p1 - q1);
        var a1 = SClip2(ShiftRight3(a + Vector128.Create((short)4)));
        var a2 = SClip2(ShiftRight3(a + Vector128.Create((short)3)));

        return (Clip1(p0 + a2), Clip1(q0 - a1));
    }

    /// <summary>4-pixels-in/4-pixels-out interior-edge filter — <c>Vp8NormalLoopFilter.DoFilter4</c>, lane for lane.</summary>
    private static (Vector128<short> P1, Vector128<short> P0, Vector128<short> Q0, Vector128<short> Q1) DoFilter4(
        Vector128<short> p1, Vector128<short> p0, Vector128<short> q0, Vector128<short> q1)
    {
        var a = Vector128.Create((short)3) * (q0 - p0);
        var a1 = SClip2(ShiftRight3(a + Vector128.Create((short)4)));
        var a2 = SClip2(ShiftRight3(a + Vector128.Create((short)3)));
        var a3 = Vector128.ShiftRightArithmetic(a1 + Vector128.Create((short)1), 1);

        return (Clip1(p1 + a3), Clip1(p0 + a2), Clip1(q0 - a1), Clip1(q1 - a3));
    }

    /// <summary>6-pixels-in/6-pixels-out macroblock-edge filter — <c>Vp8NormalLoopFilter.DoFilter6</c>, lane for lane.</summary>
    private static (Vector128<short> P2, Vector128<short> P1, Vector128<short> P0, Vector128<short> Q0, Vector128<short> Q1, Vector128<short> Q2) DoFilter6(
        Vector128<short> p2, Vector128<short> p1, Vector128<short> p0,
        Vector128<short> q0, Vector128<short> q1, Vector128<short> q2)
    {
        var a = SClip1((Vector128.Create((short)3) * (q0 - p0)) + SClip1(p1 - q1));
        var sixtyThree = Vector128.Create((short)63);
        var a1 = Vector128.ShiftRightArithmetic((Vector128.Create((short)27) * a) + sixtyThree, 7);
        var a2 = Vector128.ShiftRightArithmetic((Vector128.Create((short)18) * a) + sixtyThree, 7);
        var a3 = Vector128.ShiftRightArithmetic((Vector128.Create((short)9) * a) + sixtyThree, 7);

        return (Clip1(p2 + a3), Clip1(p1 + a2), Clip1(p0 + a1), Clip1(q0 - a1), Clip1(q1 - a2), Clip1(q2 - a3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> AbsDiff(Vector128<short> a, Vector128<short> b) => Vector128.Abs(a - b);

    /// <summary>Clamp to [-128,127] — <see cref="Vp8LoopFilterThresholds.SClip1"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> SClip1(Vector128<short> v) =>
        Vector128.Min(Vector128.Max(v, Vector128.Create((short)-128)), Vector128.Create((short)127));

    /// <summary>Clamp to [-16,15] — <see cref="Vp8LoopFilterThresholds.SClip2"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> SClip2(Vector128<short> v) =>
        Vector128.Min(Vector128.Max(v, Vector128.Create((short)-16)), Vector128.Create((short)15));

    /// <summary>Clamp to [0,255] — <see cref="Vp8LoopFilterThresholds.Clip1"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Clip1(Vector128<short> v) =>
        Vector128.Min(Vector128.Max(v, Vector128<short>.Zero), Vector128.Create((short)255));

    /// <summary>Arithmetic <c>&gt;&gt; 3</c>, matching C#'s floor-toward-negative-infinity shift on <see cref="int"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> ShiftRight3(Vector128<short> v) => Vector128.ShiftRightArithmetic(v, 3);

    private static Vector128<byte> Load(Span<byte> plane, int offset) =>
        Vector128.Create<byte>(plane.Slice(offset, Vector128<byte>.Count));

    private static Vector128<short> Lower(Vector128<byte> v) => Vector128.WidenLower(v).AsInt16();

    private static Vector128<short> Upper(Vector128<byte> v) => Vector128.WidenUpper(v).AsInt16();

    /// <summary>Narrows the two filtered halves back to bytes and writes them. Every lane is already clamped to [0,255], so the truncating narrow keeps them intact.</summary>
    private static void Store(Span<byte> plane, int offset, Vector128<short> lower, Vector128<short> upper) =>
        Vector128.Narrow(lower, upper).AsByte().CopyTo(plane.Slice(offset, Vector128<byte>.Count));
}
