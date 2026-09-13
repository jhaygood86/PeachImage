using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.IntraModel;

/// <summary>
/// SIMD tier of <see cref="IAv1Hadamard4x4Kernel"/>, ported from libaom's own <c>aom_hadamard_4x4_sse2</c>/
/// <c>hadamard_col4_sse2</c> (<c>aom_dsp/x86/avg_intrin_sse2.c</c>) -- but expressed over one
/// <see cref="Vector128{T}"/> of <see cref="int"/> per row (4 lanes) instead of libaom's int16-in-64-bit
/// packing, since this port's residual/coefficient buffers are already <see cref="int"/> end to end. The
/// butterfly math is identical either way; only the lane width differs.
///
/// <para><b>Why this produces the same bytes as <see cref="ScalarAv1Hadamard4x4Kernel"/></b>: the scalar
/// kernel's first pass computes, for each column <c>col</c>, a 4-point butterfly over that column's 4 rows
/// (<c>HadamardCol4(srcDiff, idx: col, stride: srcStride, ...)</c>). Running that exact same butterfly as one
/// vector operation per output term across <em>all four row vectors at once</em> computes, in each output
/// vector's lane <c>col</c>, precisely the scalar kernel's per-column result for that <c>col</c> -- i.e. the
/// four output vectors from a row-vector butterfly are the <em>transpose</em> of the scalar kernel's
/// intermediate <c>buffer</c>. Transposing once therefore reconstructs <c>buffer</c>'s actual rows, so a
/// second row-vector butterfly over those transposed rows reproduces the scalar kernel's second pass exactly,
/// and by the same transpose argument its output vectors are the transpose of <c>buffer2</c> -- which is
/// exactly what the scalar kernel's own final transpose step (<c>coeff[i*4+j] = buffer2[j*4+i]</c>) produces
/// as <c>coeff</c>'s rows. So this kernel's second-pass output vectors, stored directly as <c>coeff</c>'s
/// rows with no further transpose, are bit-identical to the scalar kernel's output (this matches libaom's own
/// <c>aom_hadamard_4x4_sse2</c>, which likewise transposes only once, between its two <c>hadamard_col4_sse2</c>
/// calls, and stores its second pass's output directly).</para>
///
/// <para><b>Disclosed scope narrowing</b>: libaom itself has no AVX2 (Vector256) 4x4 hadamard -- its own
/// AVX2 path only exists for 16x16 (<c>aom_hadamard_16x16_avx2</c>, which is internally four 8x8 SSE2 calls
/// plus a butterfly across their outputs, not a genuinely wider 4x4 kernel). A fixed 4x4 transform has no
/// natural 8-lane decomposition of its own, so there is no real mechanism to port at that width; this tier
/// is Vector128-only, matching libaom's own real scope exactly.</para>
/// </summary>
internal sealed class Vector128Av1Hadamard4x4Kernel : IAv1Hadamard4x4Kernel
{
    public void Apply(ReadOnlySpan<int> srcDiff, int srcStride, Span<int> coeff)
    {
        var row0 = Vector128.Create(srcDiff.Slice(0 * srcStride, 4));
        var row1 = Vector128.Create(srcDiff.Slice(1 * srcStride, 4));
        var row2 = Vector128.Create(srcDiff.Slice(2 * srcStride, 4));
        var row3 = Vector128.Create(srcDiff.Slice(3 * srcStride, 4));

        Butterfly(row0, row1, row2, row3, out var v0, out var v1, out var v2, out var v3);
        Transpose(ref v0, ref v1, ref v2, ref v3);
        Butterfly(v0, v1, v2, v3, out var w0, out var w1, out var w2, out var w3);

        w0.CopyTo(coeff.Slice(0, 4));
        w1.CopyTo(coeff.Slice(4, 4));
        w2.CopyTo(coeff.Slice(8, 4));
        w3.CopyTo(coeff.Slice(12, 4));
    }

    /// <summary>
    /// <c>hadamard_col4_sse2</c>'s butterfly, applied across four row vectors at once instead of down one
    /// column's four scalar elements -- see this class's own remarks for why that produces the transpose of
    /// the scalar kernel's per-column result rather than the result itself.
    /// </summary>
    private static void Butterfly(Vector128<int> a0, Vector128<int> a1, Vector128<int> a2, Vector128<int> a3, out Vector128<int> r0, out Vector128<int> r1, out Vector128<int> r2, out Vector128<int> r3)
    {
        var b0 = (a0 + a1) >> 1;
        var b1 = (a0 - a1) >> 1;
        var b2 = (a2 + a3) >> 1;
        var b3 = (a2 - a3) >> 1;

        r0 = b0 + b2;
        r1 = b1 + b3;
        r2 = b0 - b2;
        r3 = b1 - b3;
    }

    private static void Transpose(ref Vector128<int> r0, ref Vector128<int> r1, ref Vector128<int> r2, ref Vector128<int> r3)
    {
        var t0 = Vector128.Create(r0.GetElement(0), r1.GetElement(0), r2.GetElement(0), r3.GetElement(0));
        var t1 = Vector128.Create(r0.GetElement(1), r1.GetElement(1), r2.GetElement(1), r3.GetElement(1));
        var t2 = Vector128.Create(r0.GetElement(2), r1.GetElement(2), r2.GetElement(2), r3.GetElement(2));
        var t3 = Vector128.Create(r0.GetElement(3), r1.GetElement(3), r2.GetElement(3), r3.GetElement(3));

        r0 = t0;
        r1 = t1;
        r2 = t2;
        r3 = t3;
    }
}
