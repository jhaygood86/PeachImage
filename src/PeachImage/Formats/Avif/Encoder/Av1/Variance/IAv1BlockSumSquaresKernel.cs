namespace PeachImage.Formats.Avif.Encoder.Av1.Variance;

/// <summary>
/// Real 2D sum/sum-of-squares reduction over a <c>w x h</c> region of a
/// strided plane, matching libaom's own <c>aom_var_2d_u8</c>/<c>aom_var_2d_u16</c>
/// (<c>aom_dsp/x86/sum_squares_sse2.c</c>: <c>ss - s*s/(width*height)</c>) -- this project's own
/// <c>Av1TileEncoder.EstimatePbSourceVariance</c>/<c>ComputeLogSubBlockVariance</c> and
/// <c>Av1ScreenContentEstimator.ComputeVariance</c> already compute this identical
/// <c>sse - sum*sum/n</c> variance formula independently; this kernel is the one shared reduction step
/// underneath all three. See <see cref="ScalarAv1BlockSumSquaresKernel"/> for the reference implementation
/// every tier must match bit-exactly. Encoder-only (unlike the intra-prediction kernels): none of the three
/// call sites run during decode.
/// </summary>
internal interface IAv1BlockSumSquaresKernel
{
    /// <summary>
    /// Sums <paramref name="source"/>'s samples and their squares over the <paramref name="w"/> x
    /// <paramref name="h"/> region starting at <paramref name="source"/>[(<paramref name="y"/> *
    /// <paramref name="stride"/>) + <paramref name="x"/>].
    /// </summary>
    void Compute(ReadOnlySpan<int> source, int stride, int x, int y, int w, int h, out long sum, out long sumSquares);
}
