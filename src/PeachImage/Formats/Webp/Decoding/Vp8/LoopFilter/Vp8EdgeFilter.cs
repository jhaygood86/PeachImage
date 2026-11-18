using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PeachImage.Formats.Shared.Vectors;

namespace PeachImage.Formats.Webp.Decoding.Vp8.LoopFilter;

/// <summary>
/// Whole-edge drivers for the normal loop filter around <see cref="Vp8ByteLoopFilter.Filter"/>: each call filters one
/// sixteen-lane edge, working through <c>ref byte</c> so there is no span bounds check per row, and loading chroma
/// edges as the U and V planes side by side (eight lanes of each) so a chroma edge fills a vector the way a luma edge does.
/// </summary>
/// <remarks>
/// The caller guarantees every byte the edge reads and writes is inside the plane (the frame decoder checks the macroblock's
/// extent once, instead of every row of every edge). A vertical edge reads and writes the eight columns around it, so
/// the lanes (rows) are gathered by transposing sixteen rows of eight bytes and scattered back the same way.
/// </remarks>
internal static class Vp8EdgeFilter
{
    public static bool IsSupported => Sse2.IsSupported;

    /// <summary>Filters the 16 columns across the horizontal edge whose first row below the edge starts at <paramref name="q0Row"/>.</summary>
    public static void LumaHorizontal(ref byte q0Row, int stride, bool macroblockEdge, int thresh, int interiorLimit, int hevThreshold)
    {
        var p3 = Vector128.LoadUnsafe(ref Unsafe.Subtract(ref q0Row, 4 * stride));
        var p2 = Vector128.LoadUnsafe(ref Unsafe.Subtract(ref q0Row, 3 * stride));
        var p1 = Vector128.LoadUnsafe(ref Unsafe.Subtract(ref q0Row, 2 * stride));
        var p0 = Vector128.LoadUnsafe(ref Unsafe.Subtract(ref q0Row, stride));
        var q0 = Vector128.LoadUnsafe(ref q0Row);
        var q1 = Vector128.LoadUnsafe(ref Unsafe.Add(ref q0Row, stride));
        var q2 = Vector128.LoadUnsafe(ref Unsafe.Add(ref q0Row, 2 * stride));
        var q3 = Vector128.LoadUnsafe(ref Unsafe.Add(ref q0Row, 3 * stride));

        var r = Vp8ByteLoopFilter.Filter(p3, p2, p1, p0, q0, q1, q2, q3, thresh, interiorLimit, hevThreshold, macroblockEdge);

        if (macroblockEdge)
        {
            r.P2.StoreUnsafe(ref Unsafe.Subtract(ref q0Row, 3 * stride));
            r.Q2.StoreUnsafe(ref Unsafe.Add(ref q0Row, 2 * stride));
        }

        r.P1.StoreUnsafe(ref Unsafe.Subtract(ref q0Row, 2 * stride));
        r.P0.StoreUnsafe(ref Unsafe.Subtract(ref q0Row, stride));
        r.Q0.StoreUnsafe(ref q0Row);
        r.Q1.StoreUnsafe(ref Unsafe.Add(ref q0Row, stride));
    }

    /// <summary>Filters the 16 rows across the vertical edge whose first column right of the edge is at <paramref name="q0"/>.</summary>
    public static void LumaVertical(ref byte q0, int stride, bool macroblockEdge, int thresh, int interiorLimit, int hevThreshold)
    {
        ref byte left = ref Unsafe.Subtract(ref q0, 4);
        ref byte lower = ref Unsafe.Add(ref left, 8 * stride);
        FilterVertical(ref left, ref lower, stride, macroblockEdge, thresh, interiorLimit, hevThreshold);
    }

    /// <summary>Filters the eight columns of both chroma planes across a horizontal edge.</summary>
    public static void ChromaHorizontal(ref byte uQ0Row, ref byte vQ0Row, int stride, bool macroblockEdge, int thresh, int interiorLimit, int hevThreshold)
    {
        var p3 = LoadPair(ref uQ0Row, ref vQ0Row, -4 * stride);
        var p2 = LoadPair(ref uQ0Row, ref vQ0Row, -3 * stride);
        var p1 = LoadPair(ref uQ0Row, ref vQ0Row, -2 * stride);
        var p0 = LoadPair(ref uQ0Row, ref vQ0Row, -stride);
        var q0 = LoadPair(ref uQ0Row, ref vQ0Row, 0);
        var q1 = LoadPair(ref uQ0Row, ref vQ0Row, stride);
        var q2 = LoadPair(ref uQ0Row, ref vQ0Row, 2 * stride);
        var q3 = LoadPair(ref uQ0Row, ref vQ0Row, 3 * stride);

        var r = Vp8ByteLoopFilter.Filter(p3, p2, p1, p0, q0, q1, q2, q3, thresh, interiorLimit, hevThreshold, macroblockEdge);

        if (macroblockEdge)
        {
            StorePair(ref uQ0Row, ref vQ0Row, -3 * stride, r.P2);
            StorePair(ref uQ0Row, ref vQ0Row, 2 * stride, r.Q2);
        }

        StorePair(ref uQ0Row, ref vQ0Row, -2 * stride, r.P1);
        StorePair(ref uQ0Row, ref vQ0Row, -stride, r.P0);
        StorePair(ref uQ0Row, ref vQ0Row, 0, r.Q0);
        StorePair(ref uQ0Row, ref vQ0Row, stride, r.Q1);
    }

    /// <summary>Filters the eight rows of both chroma planes across a vertical edge.</summary>
    public static void ChromaVertical(ref byte uQ0, ref byte vQ0, int stride, bool macroblockEdge, int thresh, int interiorLimit, int hevThreshold) =>
        FilterVertical(ref Unsafe.Subtract(ref uQ0, 4), ref Unsafe.Subtract(ref vQ0, 4), stride, macroblockEdge, thresh, interiorLimit, hevThreshold);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> LoadPair(ref byte u, ref byte v, int offset) =>
        Vector128.Create(
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref u, offset)),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref v, offset))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StorePair(ref byte u, ref byte v, int offset, Vector128<byte> value)
    {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref u, offset), value.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref v, offset), value.AsUInt64().GetElement(1));
    }

    /// <summary>
    /// Filters sixteen lanes across a vertical edge: lanes 0-7 are the eight rows starting at <paramref name="upper"/>, lanes 8-15
    /// the eight rows starting at <paramref name="lower"/> (the next eight luma rows, or the V plane), each row's eight bytes
    /// being the four columns either side of the edge.
    /// </summary>
    private static void FilterVertical(ref byte upper, ref byte lower, int stride, bool macroblockEdge, int thresh, int interiorLimit, int hevThreshold)
    {
        // Rows r of both halves share a vector: bytes 0-7 from the upper block, 8-15 from the lower.
        var r0 = Row2(ref upper, ref lower, 0, stride);
        var r1 = Row2(ref upper, ref lower, 1, stride);
        var r2 = Row2(ref upper, ref lower, 2, stride);
        var r3 = Row2(ref upper, ref lower, 3, stride);
        var r4 = Row2(ref upper, ref lower, 4, stride);
        var r5 = Row2(ref upper, ref lower, 5, stride);
        var r6 = Row2(ref upper, ref lower, 6, stride);
        var r7 = Row2(ref upper, ref lower, 7, stride);

        // Byte-interleave row pairs: the low halves carry the upper block, the high halves the lower block.
        var l01 = VectorShuffle.ZipLower(r0, r1);
        var h01 = VectorShuffle.ZipUpper(r0, r1);
        var l23 = VectorShuffle.ZipLower(r2, r3);
        var h23 = VectorShuffle.ZipUpper(r2, r3);
        var l45 = VectorShuffle.ZipLower(r4, r5);
        var h45 = VectorShuffle.ZipUpper(r4, r5);
        var l67 = VectorShuffle.ZipLower(r6, r7);
        var h67 = VectorShuffle.ZipUpper(r6, r7);

        // Group them into fours of rows, then complete the eight: each 64-bit half is then one column.
        var xl0 = VectorShuffle.ZipLower(l01.AsUInt16(), l23.AsUInt16());
        var xl1 = VectorShuffle.ZipUpper(l01.AsUInt16(), l23.AsUInt16());
        var xl2 = VectorShuffle.ZipLower(l45.AsUInt16(), l67.AsUInt16());
        var xl3 = VectorShuffle.ZipUpper(l45.AsUInt16(), l67.AsUInt16());
        var xh0 = VectorShuffle.ZipLower(h01.AsUInt16(), h23.AsUInt16());
        var xh1 = VectorShuffle.ZipUpper(h01.AsUInt16(), h23.AsUInt16());
        var xh2 = VectorShuffle.ZipLower(h45.AsUInt16(), h67.AsUInt16());
        var xh3 = VectorShuffle.ZipUpper(h45.AsUInt16(), h67.AsUInt16());

        var zl0 = VectorShuffle.ZipLower(xl0.AsUInt32(), xl2.AsUInt32());
        var zl1 = VectorShuffle.ZipUpper(xl0.AsUInt32(), xl2.AsUInt32());
        var zl2 = VectorShuffle.ZipLower(xl1.AsUInt32(), xl3.AsUInt32());
        var zl3 = VectorShuffle.ZipUpper(xl1.AsUInt32(), xl3.AsUInt32());
        var zh0 = VectorShuffle.ZipLower(xh0.AsUInt32(), xh2.AsUInt32());
        var zh1 = VectorShuffle.ZipUpper(xh0.AsUInt32(), xh2.AsUInt32());
        var zh2 = VectorShuffle.ZipLower(xh1.AsUInt32(), xh3.AsUInt32());
        var zh3 = VectorShuffle.ZipUpper(xh1.AsUInt32(), xh3.AsUInt32());

        // Tap k as sixteen lanes: the upper block's column k (low half) beside the lower block's (high half).
        var p3 = VectorShuffle.ZipLower(zl0.AsUInt64(), zh0.AsUInt64()).AsByte();
        var p2 = VectorShuffle.ZipUpper(zl0.AsUInt64(), zh0.AsUInt64()).AsByte();
        var p1 = VectorShuffle.ZipLower(zl1.AsUInt64(), zh1.AsUInt64()).AsByte();
        var p0 = VectorShuffle.ZipUpper(zl1.AsUInt64(), zh1.AsUInt64()).AsByte();
        var q0 = VectorShuffle.ZipLower(zl2.AsUInt64(), zh2.AsUInt64()).AsByte();
        var q1 = VectorShuffle.ZipUpper(zl2.AsUInt64(), zh2.AsUInt64()).AsByte();
        var q2 = VectorShuffle.ZipLower(zl3.AsUInt64(), zh3.AsUInt64()).AsByte();
        var q3 = VectorShuffle.ZipUpper(zl3.AsUInt64(), zh3.AsUInt64()).AsByte();

        var r = Vp8ByteLoopFilter.Filter(p3, p2, p1, p0, q0, q1, q2, q3, thresh, interiorLimit, hevThreshold, macroblockEdge);

        // Back to rows: interleave taps in pairs, then fours, then eights; p3 and q3 are unmodified and go back as read.
        var a0 = VectorShuffle.ZipLower(p3, r.P2);
        var b0 = VectorShuffle.ZipUpper(p3, r.P2);
        var a1 = VectorShuffle.ZipLower(r.P1, r.P0);
        var b1 = VectorShuffle.ZipUpper(r.P1, r.P0);
        var a2 = VectorShuffle.ZipLower(r.Q0, r.Q1);
        var b2 = VectorShuffle.ZipUpper(r.Q0, r.Q1);
        var a3 = VectorShuffle.ZipLower(r.Q2, q3);
        var b3 = VectorShuffle.ZipUpper(r.Q2, q3);

        var c0 = VectorShuffle.ZipLower(a0.AsUInt16(), a1.AsUInt16());
        var c1 = VectorShuffle.ZipUpper(a0.AsUInt16(), a1.AsUInt16());
        var d0 = VectorShuffle.ZipLower(a2.AsUInt16(), a3.AsUInt16());
        var d1 = VectorShuffle.ZipUpper(a2.AsUInt16(), a3.AsUInt16());
        var e0 = VectorShuffle.ZipLower(b0.AsUInt16(), b1.AsUInt16());
        var e1 = VectorShuffle.ZipUpper(b0.AsUInt16(), b1.AsUInt16());
        var f0 = VectorShuffle.ZipLower(b2.AsUInt16(), b3.AsUInt16());
        var f1 = VectorShuffle.ZipUpper(b2.AsUInt16(), b3.AsUInt16());

        StoreRows(ref upper, stride, VectorShuffle.ZipLower(c0.AsUInt32(), d0.AsUInt32()), VectorShuffle.ZipUpper(c0.AsUInt32(), d0.AsUInt32()),
            VectorShuffle.ZipLower(c1.AsUInt32(), d1.AsUInt32()), VectorShuffle.ZipUpper(c1.AsUInt32(), d1.AsUInt32()));
        StoreRows(ref lower, stride, VectorShuffle.ZipLower(e0.AsUInt32(), f0.AsUInt32()), VectorShuffle.ZipUpper(e0.AsUInt32(), f0.AsUInt32()),
            VectorShuffle.ZipLower(e1.AsUInt32(), f1.AsUInt32()), VectorShuffle.ZipUpper(e1.AsUInt32(), f1.AsUInt32()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Row2(ref byte upper, ref byte lower, int row, int stride) =>
        Vector128.Create(
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref upper, row * stride)),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref lower, row * stride))).AsByte();

    /// <summary>Writes eight rows from four vectors holding two rows each (rows 0-1, 2-3, 4-5, 6-7).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRows(ref byte first, int stride, Vector128<uint> rows01, Vector128<uint> rows23, Vector128<uint> rows45, Vector128<uint> rows67)
    {
        Unsafe.WriteUnaligned(ref first, rows01.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref first, stride), rows01.AsUInt64().GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref first, 2 * stride), rows23.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref first, 3 * stride), rows23.AsUInt64().GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref first, 4 * stride), rows45.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref first, 5 * stride), rows45.AsUInt64().GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref first, 6 * stride), rows67.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref first, 7 * stride), rows67.AsUInt64().GetElement(1));
    }
}
