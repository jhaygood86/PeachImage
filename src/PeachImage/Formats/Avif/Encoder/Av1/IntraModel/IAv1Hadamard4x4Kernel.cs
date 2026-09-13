namespace PeachImage.Formats.Avif.Encoder.Av1.IntraModel;

/// <summary>
/// Real 4x4 Hadamard transform used by <see cref="Av1IntraModelRdPruner"/>'s SATD-based intra-mode shortlist
/// -- a faithful port of libaom's own <c>aom_hadamard_4x4</c> (<c>aom_dsp/avg.c</c>/<c>aom_dsp/x86/avg_intrin_sse2.c</c>).
/// See <see cref="ScalarAv1Hadamard4x4Kernel"/> for the reference implementation every tier must match
/// bit-exactly.
/// </summary>
internal interface IAv1Hadamard4x4Kernel
{
    /// <summary>
    /// Transforms the 4x4 block at <paramref name="srcDiff"/> (row-major, row stride
    /// <paramref name="srcStride"/>) into <paramref name="coeff"/> (row-major, stride 4).
    /// </summary>
    void Apply(ReadOnlySpan<int> srcDiff, int srcStride, Span<int> coeff);
}
