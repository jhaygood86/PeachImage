using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeachImage.Formats.Webp.Decoding.Vp8.LoopFilter;

/// <summary>
/// The VP8 normal loop filter on sixteen pixels per vector, in 8-bit lanes with saturating arithmetic, the shape
/// of libwebp's SSE2 filter (<c>src/dsp/dec_sse2.c</c>: <c>NeedsFilter</c>/<c>ComplexMask</c>/<c>DoFilter4</c>/
/// <c>DoFilter6</c>). <see cref="Vp8VectorLoopFilter"/>'s portable form widens to 16-bit lanes and filters the two
/// halves separately, which is about twice the instructions; this does the whole vector at once. Results are
/// identical to the scalar filter for every input (the saturating byte steps are the scalar clips; pinned by the
/// differential tests against the scalar implementation).
/// </summary>
internal static class Vp8ByteLoopFilter
{
    public static bool IsSupported => Sse2.IsSupported;

    /// <summary>
    /// Filters one edge's worth of eight tap rows (<c>p3..p0</c> before the edge, <c>q0..q3</c> after it), returning
    /// the six rows the filter may change. For an inner edge (<paramref name="macroblockEdge"/> false) only
    /// <c>p1..q1</c> change and <c>p2</c>/<c>q2</c> come back as given.
    /// <c>thresh</c> is the edge-strength limit as libwebp passes it: the scalar code's <c>4|p0-q0| + |p1-q1| &lt;= 2*thresh+1</c>
    /// gate is <c>2|p0-q0| + |p1-q1|/2 &lt;= thresh</c> here.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector128<byte> P2, Vector128<byte> P1, Vector128<byte> P0, Vector128<byte> Q0, Vector128<byte> Q1, Vector128<byte> Q2) Filter(
        Vector128<byte> p3, Vector128<byte> p2, Vector128<byte> p1, Vector128<byte> p0,
        Vector128<byte> q0, Vector128<byte> q1, Vector128<byte> q2, Vector128<byte> q3,
        int thresh, int interiorLimit, int hevThreshold, bool macroblockEdge)
    {
        var zero = Vector128<byte>.Zero;

        // Filter mask: the interior differences all within the interior limit, and the edge-strength gate.
        var interior = Sse2.Max(
            Sse2.Max(Sse2.Max(Abs(p3, p2), Abs(p2, p1)), Sse2.Max(Abs(p1, p0), Abs(q3, q2))),
            Sse2.Max(Abs(q2, q1), Abs(q1, q0)));
        var interiorOk = Sse2.CompareEqual(Sse2.SubtractSaturate(interior, Vector128.Create((byte)interiorLimit)), zero);

        var halfP1Q1 = Sse2.ShiftRightLogical(Sse2.And(Abs(p1, q1), Vector128.Create((byte)0xFE)).AsUInt16(), 1).AsByte();
        var p0q0 = Abs(p0, q0);
        var strength = Sse2.AddSaturate(Sse2.AddSaturate(p0q0, p0q0), halfP1Q1);
        var edgeOk = Sse2.CompareEqual(Sse2.SubtractSaturate(strength, Vector128.Create((byte)Math.Min(thresh, 255))), zero);
        var mask = Sse2.And(interiorOk, edgeOk);

        // High edge variance is |p1-p0| or |q1-q0| above the threshold; this is its complement.
        var notHev = Sse2.CompareEqual(
            Sse2.SubtractSaturate(Sse2.Max(Abs(p1, p0), Abs(q1, q0)), Vector128.Create((byte)hevThreshold)),
            zero);

        // The arithmetic below is on signed bytes: flip the sign bit to map 0..255 onto -128..127.
        var sign = Vector128.Create((byte)0x80);
        var p1s = Sse2.Xor(p1, sign).AsSByte();
        var p0s = Sse2.Xor(p0, sign).AsSByte();
        var q0s = Sse2.Xor(q0, sign).AsSByte();
        var q1s = Sse2.Xor(q1, sign).AsSByte();

        var k3 = Vector128.Create((sbyte)3);
        var k4 = Vector128.Create((sbyte)4);

        if (!macroblockEdge)
        {
            // hev pixels: the two-tap filter uses p1 - q1; the rest use only 3 * (q0 - p0) and also adjust p1/q1.
            var t1 = Sse2.AndNot(notHev, Sse2.SubtractSaturate(p1s, q1s).AsByte()).AsSByte();
            var q0MinusP0 = Sse2.SubtractSaturate(q0s, p0s);
            t1 = Sse2.AddSaturate(t1, q0MinusP0);
            t1 = Sse2.AddSaturate(t1, q0MinusP0);
            t1 = Sse2.AddSaturate(t1, q0MinusP0);
            t1 = Sse2.And(t1.AsByte(), mask).AsSByte();

            var a2 = SignedShift3(Sse2.AddSaturate(t1, k3));
            var a1 = SignedShift3(Sse2.AddSaturate(t1, k4));
            p0s = Sse2.AddSaturate(p0s, a2);
            q0s = Sse2.SubtractSaturate(q0s, a1);

            // (a1 + 1) >> 1 on signed bytes, via an unsigned average of the sign-flipped value.
            var a3 = Sse2.Subtract(Sse2.Average(Sse2.Add(a1.AsByte(), sign), zero), Vector128.Create((byte)64)).AsSByte();
            a3 = Sse2.And(notHev, a3.AsByte()).AsSByte();
            q1s = Sse2.SubtractSaturate(q1s, a3);
            p1s = Sse2.AddSaturate(p1s, a3);

            return (p2, Sse2.Xor(p1s.AsByte(), sign), Sse2.Xor(p0s.AsByte(), sign), Sse2.Xor(q0s.AsByte(), sign), Sse2.Xor(q1s.AsByte(), sign), q2);
        }

        var p2s = Sse2.Xor(p2, sign).AsSByte();
        var q2s = Sse2.Xor(q2, sign).AsSByte();

        // base delta = p1 - q1 + 3 * (q0 - p0), saturating at each step.
        var p1MinusQ1 = Sse2.SubtractSaturate(p1s, q1s);
        var q0MinusP0Mb = Sse2.SubtractSaturate(q0s, p0s);
        var s1 = Sse2.AddSaturate(p1MinusQ1, q0MinusP0Mb);
        var s2 = Sse2.AddSaturate(q0MinusP0Mb, s1);
        var delta = Sse2.AddSaturate(q0MinusP0Mb, s2);

        // hev pixels get the simple filter on p0/q0.
        var simple = Sse2.And(delta.AsByte(), Sse2.AndNot(notHev, mask)).AsSByte();
        var v3 = SignedShift3(Sse2.AddSaturate(simple, k3));
        var v4 = SignedShift3(Sse2.AddSaturate(simple, k4));
        q0s = Sse2.SubtractSaturate(q0s, v4);
        p0s = Sse2.AddSaturate(p0s, v3);

        // The rest get the strong filter across three pixels either side: 27/18/9 times the delta (+63) >> 7.
        var strong = Sse2.And(delta.AsByte(), Sse2.And(notHev, mask));
        var lo = Sse2.UnpackLow(zero, strong).AsInt16();
        var hi = Sse2.UnpackHigh(zero, strong).AsInt16();
        var k9 = Vector128.Create((short)0x0900);
        var k63 = Vector128.Create((short)63);
        var f9Lo = Sse2.MultiplyHigh(lo, k9);
        var f9Hi = Sse2.MultiplyHigh(hi, k9);
        var a2Lo = Sse2.Add(f9Lo, k63);
        var a2Hi = Sse2.Add(f9Hi, k63);
        var a1Lo = Sse2.Add(a2Lo, f9Lo);
        var a1Hi = Sse2.Add(a2Hi, f9Hi);
        var a0Lo = Sse2.Add(a1Lo, f9Lo);
        var a0Hi = Sse2.Add(a1Hi, f9Hi);

        Update2(ref p2s, ref q2s, a2Lo, a2Hi);
        Update2(ref p1s, ref q1s, a1Lo, a1Hi);
        Update2(ref p0s, ref q0s, a0Lo, a0Hi);

        return (
            Sse2.Xor(p2s.AsByte(), sign), Sse2.Xor(p1s.AsByte(), sign), Sse2.Xor(p0s.AsByte(), sign),
            Sse2.Xor(q0s.AsByte(), sign), Sse2.Xor(q1s.AsByte(), sign), Sse2.Xor(q2s.AsByte(), sign));
    }

    /// <summary>Absolute difference of unsigned bytes: the OR of the two saturating subtractions.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Abs(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.Or(Sse2.SubtractSaturate(a, b), Sse2.SubtractSaturate(b, a));

    /// <summary>Arithmetic shift right by 3 of each signed byte: place it in a 16-bit lane's high byte, shift, repack.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> SignedShift3(Vector128<sbyte> x)
    {
        var zero = Vector128<byte>.Zero;
        var lo = Sse2.ShiftRightArithmetic(Sse2.UnpackLow(zero, x.AsByte()).AsInt16(), 3 + 8);
        var hi = Sse2.ShiftRightArithmetic(Sse2.UnpackHigh(zero, x.AsByte()).AsInt16(), 3 + 8);
        return Sse2.PackSignedSaturate(lo, hi);
    }

    /// <summary>p += (a >> 7), q -= (a >> 7) with a given as sixteen-bit lanes for the low and high eight pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Update2(ref Vector128<sbyte> p, ref Vector128<sbyte> q, Vector128<short> aLo, Vector128<short> aHi)
    {
        var delta = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(aLo, 7), Sse2.ShiftRightArithmetic(aHi, 7));
        p = Sse2.AddSaturate(p, delta);
        q = Sse2.SubtractSaturate(q, delta);
    }
}
