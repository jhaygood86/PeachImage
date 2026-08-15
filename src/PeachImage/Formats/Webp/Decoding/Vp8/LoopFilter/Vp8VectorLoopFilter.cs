using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeachImage.Formats.Webp.Decoding.Vp8.LoopFilter;

/// <summary>
/// Vectorized form of <see cref="Vp8NormalLoopFilter"/>'s per-lane work, for both edge orientations.
/// </summary>
/// <remarks>
/// <para>
/// The deblocking filter was measured at 32% of lossy decode — the single largest bucket, ahead of entropy
/// decode. It splits roughly evenly between the two edge orientations. A "top" edge (a horizontal edge, filtered
/// vertically) walks its 16 or 8 lanes with <c>alongStep == 1</c>, so the lanes are adjacent bytes and each
/// lane's eight taps sit at fixed row offsets: eight plain vector loads, no gathering
/// (<see cref="FilterContiguousEdge"/>). A "left" edge is the transpose of that — its lanes are a row-stride
/// apart, with each lane's eight taps contiguous instead — so it is gathered by transposing a 16x8 block in,
/// filtering it identically, and transposing back (<see cref="FilterStridedEdge"/>). Both share
/// <see cref="FilterHalf"/>, so there is one copy of the filter arithmetic.
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
    /// <summary>Taps each lane reads and writes: p3..p0, q0..q3.</summary>
    private const int Taps = 8;

    /// <summary>Lanes in a chroma edge. Half a vector's worth, so these run one <see cref="FilterHalf"/> instead of two.</summary>
    private const int ChromaLanes = 8;

    /// <summary>
    /// Whether <see cref="FilterStridedEdge"/> can handle an edge with these dimensions and this hardware.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="Sse2"/> specifically, not just <see cref="Vector128"/> acceleration. The transpose
    /// this orientation needs is built from two-vector interleaves, and .NET's portable vector API has no
    /// equivalent of them — <c>Vector128.Shuffle</c> permutes within a single vector only. Expressing the
    /// transpose portably would take roughly 120 shuffle/or operations against the ~24 interleaves used here,
    /// which is the same reason Jpeg's <c>AanVectorButterfly.Transpose8x8</c> reaches for real intrinsics. An
    /// <c>AdvSimd.Arm64.ZipLow</c>/<c>ZipHigh</c> path would be the direct Arm equivalent and is a
    /// straightforward follow-up; until then Arm falls back to the scalar filter.
    /// </remarks>
    public static bool CanFilterStrided(int size, int origin, int stride, int planeLength) =>
        Sse2.IsSupported
        && (size == Vector128<byte>.Count || size == ChromaLanes)
        && origin - (Taps / 2) >= 0
        && origin + ((size - 1) * stride) + (Taps / 2) <= planeLength;

    /// <summary>Whether <see cref="FilterContiguousEdge"/> can handle an edge with these dimensions and this hardware.</summary>
    public static bool CanFilter(int size, int origin, int stride, int planeLength) =>
        Vector128.IsHardwareAccelerated
        && (size == Vector128<byte>.Count || size == ChromaLanes)
        && origin - (4 * stride) >= 0
        && origin + (3 * stride) + size <= planeLength;

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
        int size,
        int thresh,
        int interiorLimit,
        int hevThreshold,
        bool macroblockEdge)
    {
        if (size == ChromaLanes)
        {
            FilterContiguousChromaEdge(plane, origin, stride, thresh, interiorLimit, hevThreshold, macroblockEdge);
            return;
        }

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

    /// <summary>
    /// The <see cref="ChromaLanes"/>-lane form of <see cref="FilterContiguousEdge"/>. Chroma edges are half a
    /// vector wide, so each tap row is a single 8-byte load and one <see cref="FilterHalf"/> covers the whole
    /// edge.
    /// </summary>
    private static void FilterContiguousChromaEdge(
        Span<byte> plane,
        int origin,
        int stride,
        int thresh,
        int interiorLimit,
        int hevThreshold,
        bool macroblockEdge)
    {
        var (p2, p1, p0, q0, q1, q2) = FilterHalf(
            LoadChromaRow(plane, origin - (4 * stride)),
            LoadChromaRow(plane, origin - (3 * stride)),
            LoadChromaRow(plane, origin - (2 * stride)),
            LoadChromaRow(plane, origin - stride),
            LoadChromaRow(plane, origin),
            LoadChromaRow(plane, origin + stride),
            LoadChromaRow(plane, origin + (2 * stride)),
            LoadChromaRow(plane, origin + (3 * stride)),
            Vector128.Create((short)((2 * thresh) + 1)),
            Vector128.Create((short)interiorLimit),
            Vector128.Create((short)hevThreshold),
            macroblockEdge);

        if (macroblockEdge)
        {
            StoreChromaRow(plane, origin - (3 * stride), p2);
            StoreChromaRow(plane, origin + (2 * stride), q2);
        }

        StoreChromaRow(plane, origin - (2 * stride), p1);
        StoreChromaRow(plane, origin - stride, p0);
        StoreChromaRow(plane, origin, q0);
        StoreChromaRow(plane, origin + stride, q1);
    }

    /// <summary>
    /// Filters one vertical edge, whose <see cref="Vector128{T}.Count"/> lanes are a <paramref name="stride"/>
    /// apart. Each lane's eight taps are contiguous, so the lanes are gathered by transposing a 16x8 block in,
    /// filtering it exactly as the contiguous orientation does, and transposing back out.
    /// </summary>
    /// <remarks>
    /// All eight taps are written back even though the widest filter only modifies six: p3 and q3 are returned
    /// unchanged, so storing them costs a store and changes nothing, and it keeps the store path a single
    /// uniform 8-byte write per lane.
    /// </remarks>
    public static void FilterStridedEdge(
        Span<byte> plane,
        int origin,
        int stride,
        int size,
        int thresh,
        int interiorLimit,
        int hevThreshold,
        bool macroblockEdge)
    {
        if (size == ChromaLanes)
        {
            FilterStridedChromaEdge(plane, origin, stride, thresh, interiorLimit, hevThreshold, macroblockEdge);
            return;
        }

        int first = origin - (Taps / 2);
        Span<Vector128<byte>> taps = stackalloc Vector128<byte>[Taps];
        Transpose16x8(plane, first, stride, taps);

        var thresh2 = Vector128.Create((short)((2 * thresh) + 1));
        var it = Vector128.Create((short)interiorLimit);
        var hev = Vector128.Create((short)hevThreshold);

        var (p2Lo, p1Lo, p0Lo, q0Lo, q1Lo, q2Lo) = FilterHalf(
            Lower(taps[0]), Lower(taps[1]), Lower(taps[2]), Lower(taps[3]),
            Lower(taps[4]), Lower(taps[5]), Lower(taps[6]), Lower(taps[7]),
            thresh2, it, hev, macroblockEdge);

        var (p2Hi, p1Hi, p0Hi, q0Hi, q1Hi, q2Hi) = FilterHalf(
            Upper(taps[0]), Upper(taps[1]), Upper(taps[2]), Upper(taps[3]),
            Upper(taps[4]), Upper(taps[5]), Upper(taps[6]), Upper(taps[7]),
            thresh2, it, hev, macroblockEdge);

        taps[1] = Vector128.Narrow(p2Lo, p2Hi).AsByte();
        taps[2] = Vector128.Narrow(p1Lo, p1Hi).AsByte();
        taps[3] = Vector128.Narrow(p0Lo, p0Hi).AsByte();
        taps[4] = Vector128.Narrow(q0Lo, q0Hi).AsByte();
        taps[5] = Vector128.Narrow(q1Lo, q1Hi).AsByte();
        taps[6] = Vector128.Narrow(q2Lo, q2Hi).AsByte();

        Transpose8x16(taps, plane, first, stride);
    }

    /// <summary>
    /// The <see cref="ChromaLanes"/>-lane form of <see cref="FilterStridedEdge"/>. A chroma edge is exactly the
    /// 8x8 block <see cref="TransposeEightRows"/> already produces, so the transpose in and out needs no extra
    /// machinery — each tap column lands in one half of a returned vector.
    /// </summary>
    private static void FilterStridedChromaEdge(
        Span<byte> plane,
        int origin,
        int stride,
        int thresh,
        int interiorLimit,
        int hevThreshold,
        bool macroblockEdge)
    {
        int first = origin - (Taps / 2);
        var (c01, c23, c45, c67) = TransposeEightRows(plane, first, stride);

        var (p2, p1, p0, q0, q1, q2) = FilterHalf(
            WidenLane(c01.GetLower()), WidenLane(c01.GetUpper()),
            WidenLane(c23.GetLower()), WidenLane(c23.GetUpper()),
            WidenLane(c45.GetLower()), WidenLane(c45.GetUpper()),
            WidenLane(c67.GetLower()), WidenLane(c67.GetUpper()),
            Vector128.Create((short)((2 * thresh) + 1)),
            Vector128.Create((short)interiorLimit),
            Vector128.Create((short)hevThreshold),
            macroblockEdge);

        // p3 and q3 are unmodified by every filter, so they go back exactly as they were read.
        var t0 = Vector128.Create(c01.GetLower(), c01.GetLower());
        var t1 = NarrowLane(p2);
        var t2 = NarrowLane(p1);
        var t3 = NarrowLane(p0);
        var t4 = NarrowLane(q0);
        var t5 = NarrowLane(q1);
        var t6 = NarrowLane(q2);
        var t7 = Vector128.Create(c67.GetUpper(), c67.GetUpper());

        StoreEightRows(
            Sse2.UnpackLow(t0, t1),
            Sse2.UnpackLow(t2, t3),
            Sse2.UnpackLow(t4, t5),
            Sse2.UnpackLow(t6, t7),
            plane,
            first,
            stride);
    }

    /// <summary>
    /// Gathers <see cref="Taps"/> bytes from each of 16 rows into <see cref="Taps"/> vectors, one per tap
    /// position, so lane <c>i</c> of every output holds row <c>i</c>'s value for that tap.
    /// </summary>
    /// <remarks>
    /// Standard interleave-ladder transpose, run as two independent 8-row halves that are then stitched
    /// together: byte interleaves pair adjacent rows, 16-bit interleaves group them into fours, and 32-bit
    /// interleaves complete the eight, at which point each 64-bit half of a vector is one whole tap column.
    /// </remarks>
    private static void Transpose16x8(Span<byte> plane, int first, int stride, Span<Vector128<byte>> taps)
    {
        var (v0, v1, v2, v3) = TransposeEightRows(plane, first, stride);
        var (w0, w1, w2, w3) = TransposeEightRows(plane, first + (8 * stride), stride);

        taps[0] = Vector128.Create(v0.GetLower(), w0.GetLower());
        taps[1] = Vector128.Create(v0.GetUpper(), w0.GetUpper());
        taps[2] = Vector128.Create(v1.GetLower(), w1.GetLower());
        taps[3] = Vector128.Create(v1.GetUpper(), w1.GetUpper());
        taps[4] = Vector128.Create(v2.GetLower(), w2.GetLower());
        taps[5] = Vector128.Create(v2.GetUpper(), w2.GetUpper());
        taps[6] = Vector128.Create(v3.GetLower(), w3.GetLower());
        taps[7] = Vector128.Create(v3.GetUpper(), w3.GetUpper());
    }

    /// <summary>Transposes eight rows of <see cref="Taps"/> bytes; each returned vector holds two tap columns, low half then high.</summary>
    private static (Vector128<byte> C01, Vector128<byte> C23, Vector128<byte> C45, Vector128<byte> C67) TransposeEightRows(
        Span<byte> plane, int first, int stride)
    {
        var t0 = Sse2.UnpackLow(LoadRow(plane, first), LoadRow(plane, first + stride));
        var t1 = Sse2.UnpackLow(LoadRow(plane, first + (2 * stride)), LoadRow(plane, first + (3 * stride)));
        var t2 = Sse2.UnpackLow(LoadRow(plane, first + (4 * stride)), LoadRow(plane, first + (5 * stride)));
        var t3 = Sse2.UnpackLow(LoadRow(plane, first + (6 * stride)), LoadRow(plane, first + (7 * stride)));

        var u0 = Sse2.UnpackLow(t0.AsUInt16(), t1.AsUInt16());
        var u1 = Sse2.UnpackHigh(t0.AsUInt16(), t1.AsUInt16());
        var u2 = Sse2.UnpackLow(t2.AsUInt16(), t3.AsUInt16());
        var u3 = Sse2.UnpackHigh(t2.AsUInt16(), t3.AsUInt16());

        return (
            Sse2.UnpackLow(u0.AsUInt32(), u2.AsUInt32()).AsByte(),
            Sse2.UnpackHigh(u0.AsUInt32(), u2.AsUInt32()).AsByte(),
            Sse2.UnpackLow(u1.AsUInt32(), u3.AsUInt32()).AsByte(),
            Sse2.UnpackHigh(u1.AsUInt32(), u3.AsUInt32()).AsByte());
    }

    /// <summary>The inverse of <see cref="Transpose16x8"/>: scatters the tap columns back out as 16 rows of <see cref="Taps"/> bytes.</summary>
    private static void Transpose8x16(ReadOnlySpan<Vector128<byte>> taps, Span<byte> plane, int first, int stride)
    {
        var a0 = Sse2.UnpackLow(taps[0], taps[1]);
        var a1 = Sse2.UnpackLow(taps[2], taps[3]);
        var a2 = Sse2.UnpackLow(taps[4], taps[5]);
        var a3 = Sse2.UnpackLow(taps[6], taps[7]);

        var b0 = Sse2.UnpackHigh(taps[0], taps[1]);
        var b1 = Sse2.UnpackHigh(taps[2], taps[3]);
        var b2 = Sse2.UnpackHigh(taps[4], taps[5]);
        var b3 = Sse2.UnpackHigh(taps[6], taps[7]);

        StoreEightRows(a0, a1, a2, a3, plane, first, stride);
        StoreEightRows(b0, b1, b2, b3, plane, first + (8 * stride), stride);
    }

    private static void StoreEightRows(
        Vector128<byte> a0, Vector128<byte> a1, Vector128<byte> a2, Vector128<byte> a3,
        Span<byte> plane, int first, int stride)
    {
        var d0 = Sse2.UnpackLow(a0.AsUInt16(), a1.AsUInt16());
        var d1 = Sse2.UnpackHigh(a0.AsUInt16(), a1.AsUInt16());
        var e0 = Sse2.UnpackLow(a2.AsUInt16(), a3.AsUInt16());
        var e1 = Sse2.UnpackHigh(a2.AsUInt16(), a3.AsUInt16());

        StoreRowPair(Sse2.UnpackLow(d0.AsUInt32(), e0.AsUInt32()).AsUInt64(), plane, first, stride);
        StoreRowPair(Sse2.UnpackHigh(d0.AsUInt32(), e0.AsUInt32()).AsUInt64(), plane, first + (2 * stride), stride);
        StoreRowPair(Sse2.UnpackLow(d1.AsUInt32(), e1.AsUInt32()).AsUInt64(), plane, first + (4 * stride), stride);
        StoreRowPair(Sse2.UnpackHigh(d1.AsUInt32(), e1.AsUInt32()).AsUInt64(), plane, first + (6 * stride), stride);
    }

    /// <summary>Reads one lane's <see cref="Taps"/> taps into the low half of a vector; the high half is unused by the interleave ladder.</summary>
    private static Vector128<byte> LoadRow(Span<byte> plane, int offset) =>
        Vector128.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(plane.Slice(offset, Taps))).AsByte();

    /// <summary>Writes the two lanes packed into <paramref name="rows"/> back to consecutive strided rows.</summary>
    private static void StoreRowPair(Vector128<ulong> rows, Span<byte> plane, int first, int stride)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(plane.Slice(first, Taps), rows.ToScalar());
        BinaryPrimitives.WriteUInt64LittleEndian(plane.Slice(first + stride, Taps), rows.GetElement(1));
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

    /// <summary>Reads one chroma tap row — <see cref="ChromaLanes"/> contiguous bytes — widened to 16-bit lanes.</summary>
    private static Vector128<short> LoadChromaRow(Span<byte> plane, int offset) =>
        Lower(Vector128.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(plane.Slice(offset, ChromaLanes))).AsByte());

    /// <summary>Widens <see cref="ChromaLanes"/> bytes held in a half-vector to 16-bit lanes.</summary>
    private static Vector128<short> WidenLane(Vector64<byte> lane) => Lower(Vector128.Create(lane, lane));

    /// <summary>Narrows a filtered chroma lane back to bytes; only the low <see cref="ChromaLanes"/> are meaningful, which is all the interleaves that follow consume.</summary>
    private static Vector128<byte> NarrowLane(Vector128<short> lane) => Vector128.Narrow(lane, lane).AsByte();

    /// <summary>Writes one filtered chroma tap row back as <see cref="ChromaLanes"/> contiguous bytes.</summary>
    private static void StoreChromaRow(Span<byte> plane, int offset, Vector128<short> lane) =>
        BinaryPrimitives.WriteUInt64LittleEndian(plane.Slice(offset, ChromaLanes), NarrowLane(lane).AsUInt64().ToScalar());

    private static Vector128<short> Upper(Vector128<byte> v) => Vector128.WidenUpper(v).AsInt16();

    /// <summary>Narrows the two filtered halves back to bytes and writes them. Every lane is already clamped to [0,255], so the truncating narrow keeps them intact.</summary>
    private static void Store(Span<byte> plane, int offset, Vector128<short> lower, Vector128<short> upper) =>
        Vector128.Narrow(lower, upper).AsByte().CopyTo(plane.Slice(offset, Vector128<byte>.Count));
}
