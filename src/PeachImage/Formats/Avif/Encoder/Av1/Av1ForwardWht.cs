namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Bit-exact integer forward 4x4 Walsh-Hadamard transform for AV1 lossless coding -- the exact algebraic
/// inverse of <see cref="Decoding.Av1.Av1InverseTransform.InverseWht"/> as driven by
/// <see cref="Decoding.Av1.Av1InverseTransform.Inverse2D"/> at <c>lossless == true</c> (row pass first with
/// shift 2, then column pass with shift 0 -- see that method's remarks). Unlike <see cref="Av1ForwardTransform"/>,
/// this cannot be built by floating-point-probing the inverse: lossless has no quantization step to mask
/// rounding error, so the forward transform must round-trip exactly for every integer input, not just
/// approximately for typical 8-bit residuals. This is the same reversible Walsh-Hadamard lifting scheme AV1
/// and VP9 both use for their lossless coding path (an integer transform designed to be exactly invertible,
/// not a linear approximation of one) -- <see cref="ForwardButterfly"/> is the precise mirror of
/// <see cref="Decoding.Av1.Av1InverseTransform.InverseWht"/>'s own butterfly, permuted the same way in
/// reverse (that method reads its input permuted and writes it straight; this one reads straight and writes
/// permuted), and <see cref="Forward4x4"/> reverses <c>Inverse2D</c>'s row-then-column order into
/// column-then-row, with the row pass scaled by 4 to exactly compensate <c>InverseWht</c>'s row-pass
/// right-shift by 2 (mirroring <c>Av1Dequantizer</c>/decoder terminology's <c>UNIT_QUANT_FACTOR</c>).
/// Verified by round-trip tests against the real <see cref="Decoding.Av1.Av1InverseTransform.Inverse2D"/>,
/// not just against this class's own logic.
/// </summary>
internal static class Av1ForwardWht
{
    private const int UnitQuantFactor = 4;

    /// <summary>
    /// Forward-transforms a 4x4 <paramref name="residual"/> (flat, row-major, e.g. <c>source - prediction</c>)
    /// into <paramref name="coeffOut"/> (same shape), such that
    /// <c>Av1InverseTransform.Inverse2D(coeffOut, ..., lossless: true, ...)</c> reproduces
    /// <paramref name="residual"/> exactly, for any integer input (no clamping/rounding loss).
    /// </summary>
    public static void Forward4x4(ReadOnlySpan<int> residual, Span<int> coeffOut)
    {
        Span<int> intermediate = stackalloc int[16];
        Span<int> line = stackalloc int[4];

        // Column pass first (reverses Inverse2D's column pass, which runs *second* with shift 0) -- no scale.
        for (int j = 0; j < 4; j++)
        {
            line[0] = residual[j];
            line[1] = residual[4 + j];
            line[2] = residual[8 + j];
            line[3] = residual[12 + j];

            ForwardButterfly(line);

            intermediate[j] = line[0];
            intermediate[4 + j] = line[1];
            intermediate[8 + j] = line[2];
            intermediate[12 + j] = line[3];
        }

        // Row pass second (reverses Inverse2D's row pass, which runs *first* with shift 2) -- scaled by
        // UnitQuantFactor (4) so InverseWht's `>> 2` on read exactly recovers these values with no loss.
        for (int i = 0; i < 4; i++)
        {
            int rowBase = i * 4;
            line[0] = intermediate[rowBase];
            line[1] = intermediate[rowBase + 1];
            line[2] = intermediate[rowBase + 2];
            line[3] = intermediate[rowBase + 3];

            ForwardButterfly(line);

            coeffOut[rowBase] = line[0] * UnitQuantFactor;
            coeffOut[rowBase + 1] = line[1] * UnitQuantFactor;
            coeffOut[rowBase + 2] = line[2] * UnitQuantFactor;
            coeffOut[rowBase + 3] = line[3] * UnitQuantFactor;
        }
    }

    /// <summary>
    /// One 1D 4-point forward Hadamard lifting step -- reads <paramref name="t"/> straight (<c>a=t[0], b=t[1],
    /// c=t[2], d=t[3]</c>) and writes it permuted (<c>t[0]=a, t[1]=c, t[2]=d, t[3]=b</c>), the exact mirror of
    /// <see cref="Decoding.Av1.Av1InverseTransform.InverseWht"/>'s own permutation (which reads permuted --
    /// <c>a=t[0], c=t[1], d=t[2], b=t[3]</c> -- and writes straight). Uses <see langword="long"/> intermediates
    /// even though the AV1 spec's own inverse butterfly stays in 32-bit range, purely to avoid this forward
    /// direction's <c>a += b</c>/<c>d -= c</c> steps ever overflowing <see langword="int"/> for pathological
    /// (out-of-range-for-real-8-bit-residuals) inputs; final results are cast back to <see langword="int"/>.
    /// </summary>
    private static void ForwardButterfly(Span<int> t)
    {
        long a = t[0];
        long b = t[1];
        long c = t[2];
        long d = t[3];

        a += b;
        d -= c;
        long e = (a - d) >> 1;
        b = e - b;
        c = e - c;
        a -= c;
        d += b;

        t[0] = (int)a;
        t[1] = (int)c;
        t[2] = (int)d;
        t[3] = (int)b;
    }
}
