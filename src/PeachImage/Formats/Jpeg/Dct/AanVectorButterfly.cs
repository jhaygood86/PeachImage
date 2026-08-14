using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// Eight <see cref="Vector256{T}"/> registers holding one 8x8 float block, one register per row (or, after
/// <see cref="AanVectorButterfly.Transpose8x8"/>, one register per column). Named fields rather than an
/// indexable array or <c>stackalloc</c> span — required so the JIT can keep all eight values in registers
/// across an inlined <see cref="AanVectorButterfly.AanInverse1D"/>/<see cref="AanVectorButterfly.AanForward1D"/>
/// call instead of spilling to the stack.
/// </summary>
internal struct Vec8
{
    public Vector256<float> V0;
    public Vector256<float> V1;
    public Vector256<float> V2;
    public Vector256<float> V3;
    public Vector256<float> V4;
    public Vector256<float> V5;
    public Vector256<float> V6;
    public Vector256<float> V7;
}

/// <summary>
/// The AAN (Arai-Agui-Nakajima) 1D butterfly passes and the 8x8 transpose needed to apply them along both
/// axes of a block, vectorized across <see cref="Vector256{T}"/> lanes. Each lane is an independent 1D
/// transform — <see cref="AanInverse1D"/>/<see cref="AanForward1D"/> are line-for-line transcriptions of
/// <see cref="AanScalarInverseDct"/>'s <c>Inverse1D</c> and <see cref="AanScalarForwardDct"/>'s
/// <c>Forward1D</c>, with every scalar <c>float</c> operation replaced by the identical
/// <see cref="Vector256{T}"/> operation. Do not re-derive the butterfly wiring here; if the scalar kernels
/// change, transcribe the change instead.
/// </summary>
/// <remarks>
/// <see cref="Transpose8x8"/> is the one place in this codebase that uses <see cref="Avx"/>'s
/// platform-specific intrinsics instead of the portable <see cref="Vector256"/> generic API: there is no
/// portable two-input shuffle/permute in that API, so a fully portable transpose would cost roughly 3x more
/// instructions here, eating most of the win this kernel exists for. This confines the AVX2 hardware
/// dependency to a single, independently testable method; kernels using it must gate on
/// <see cref="Avx.IsSupported"/> and fall back to the scalar AAN kernel otherwise (e.g. on ARM64).
/// </remarks>
internal static class AanVectorButterfly
{
    // Derived from the same double-precision Math.Cos calls as AanScalarInverseDct/AanScalarForwardDct's
    // constants (not transcribed decimal literals), then broadcast to every lane, so these are guaranteed
    // bit-identical (after the single float rounding) to the scalar kernels' constants.
    private static readonly double RawC2 = Math.Cos(2 * Math.PI / 16.0);
    private static readonly double RawC4 = Math.Cos(4 * Math.PI / 16.0);
    private static readonly double RawC6 = Math.Cos(6 * Math.PI / 16.0);

    private static readonly Vector256<float> Sqrt2 = Vector256.Create((float)Math.Sqrt(2.0));
    private static readonly Vector256<float> TwoC2 = Vector256.Create((float)(2.0 * RawC2));
    private static readonly Vector256<float> TwoC2MinusC6 = Vector256.Create((float)(2.0 * (RawC2 - RawC6)));
    private static readonly Vector256<float> TwoC2PlusC6 = Vector256.Create((float)(2.0 * (RawC2 + RawC6)));

    private static readonly Vector256<float> C4 = Vector256.Create((float)RawC4);
    private static readonly Vector256<float> C6 = Vector256.Create((float)RawC6);
    private static readonly Vector256<float> C2MinusC6 = Vector256.Create((float)(RawC2 - RawC6));
    private static readonly Vector256<float> C2PlusC6 = Vector256.Create((float)(RawC2 + RawC6));

    /// <summary>
    /// One 1D AAN inverse pass, applied lane-wise: line-for-line identical to
    /// <see cref="AanScalarInverseDct"/>'s <c>Inverse1D</c>, now doing eight independent transforms per
    /// instruction (one per SIMD lane) instead of one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AanInverse1D(ref Vec8 m)
    {
        var e0 = m.V0 + m.V4;
        var e1 = m.V0 - m.V4;
        var e3 = m.V2 + m.V6;
        var e2 = ((m.V2 - m.V6) * Sqrt2) - e3;

        var a0 = e0 + e3;
        var a3 = e0 - e3;
        var a1 = e1 + e2;
        var a2 = e1 - e2;

        var z13 = m.V5 + m.V3;
        var z10 = m.V5 - m.V3;
        var z11 = m.V1 + m.V7;
        var z12 = m.V1 - m.V7;

        var z5 = (z10 + z12) * TwoC2;
        var o10 = z5 - (z12 * TwoC2MinusC6);
        var o12 = z5 - (z10 * TwoC2PlusC6);

        var o7 = z11 + z13;
        var o11 = (z11 - z13) * Sqrt2;

        var o6 = o12 - o7;
        var o5 = o11 - o6;
        var o4 = o10 - o5;

        m.V0 = a0 + o7;
        m.V7 = a0 - o7;
        m.V1 = a1 + o6;
        m.V6 = a1 - o6;
        m.V2 = a2 + o5;
        m.V5 = a2 - o5;
        m.V3 = a3 + o4;
        m.V4 = a3 - o4;
    }

    /// <summary>
    /// One 1D AAN forward pass, applied lane-wise: line-for-line identical to
    /// <see cref="AanScalarForwardDct"/>'s <c>Forward1D</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AanForward1D(ref Vec8 m)
    {
        var s0 = m.V0 + m.V7;
        var d0 = m.V0 - m.V7;
        var s1 = m.V1 + m.V6;
        var d1 = m.V1 - m.V6;
        var s2 = m.V2 + m.V5;
        var d2 = m.V2 - m.V5;
        var s3 = m.V3 + m.V4;
        var d3 = m.V3 - m.V4;

        var t10 = s0 + s3;
        var t13 = s0 - s3;
        var t11 = s1 + s2;
        var t12 = s1 - s2;

        var r0 = t10 + t11;
        var r4 = t10 - t11;

        var z1 = (t12 + t13) * C4;
        var r2 = t13 + z1;
        var r6 = t13 - z1;

        var o10 = d3 + d2;
        var o11 = d2 + d1;
        var o12 = d1 + d0;

        var z5 = (o10 - o12) * C6;
        var z2 = (o10 * C2MinusC6) + z5;
        var z4 = (o12 * C2PlusC6) + z5;
        var z3 = o11 * C4;

        var z11 = d0 + z3;
        var z13 = d0 - z3;

        m.V0 = r0;
        m.V1 = z11 + z4;
        m.V2 = r2;
        m.V3 = z13 - z2;
        m.V4 = r4;
        m.V5 = z13 + z2;
        m.V6 = r6;
        m.V7 = z11 - z4;
    }

    /// <summary>Transposes the 8x8 float matrix held one row per register. Standard AVX unpack/shuffle/permute recipe.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Transpose8x8(ref Vec8 m)
    {
        var t0 = Avx.UnpackLow(m.V0, m.V1);
        var t1 = Avx.UnpackHigh(m.V0, m.V1);
        var t2 = Avx.UnpackLow(m.V2, m.V3);
        var t3 = Avx.UnpackHigh(m.V2, m.V3);
        var t4 = Avx.UnpackLow(m.V4, m.V5);
        var t5 = Avx.UnpackHigh(m.V4, m.V5);
        var t6 = Avx.UnpackLow(m.V6, m.V7);
        var t7 = Avx.UnpackHigh(m.V6, m.V7);

        var tt0 = Avx.Shuffle(t0, t2, 0x44);
        var tt1 = Avx.Shuffle(t0, t2, 0xEE);
        var tt2 = Avx.Shuffle(t1, t3, 0x44);
        var tt3 = Avx.Shuffle(t1, t3, 0xEE);
        var tt4 = Avx.Shuffle(t4, t6, 0x44);
        var tt5 = Avx.Shuffle(t4, t6, 0xEE);
        var tt6 = Avx.Shuffle(t5, t7, 0x44);
        var tt7 = Avx.Shuffle(t5, t7, 0xEE);

        m.V0 = Avx.Permute2x128(tt0, tt4, 0x20);
        m.V1 = Avx.Permute2x128(tt1, tt5, 0x20);
        m.V2 = Avx.Permute2x128(tt2, tt6, 0x20);
        m.V3 = Avx.Permute2x128(tt3, tt7, 0x20);
        m.V4 = Avx.Permute2x128(tt0, tt4, 0x31);
        m.V5 = Avx.Permute2x128(tt1, tt5, 0x31);
        m.V6 = Avx.Permute2x128(tt2, tt6, 0x31);
        m.V7 = Avx.Permute2x128(tt3, tt7, 0x31);
    }
}
