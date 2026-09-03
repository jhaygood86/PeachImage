namespace PeachImage.Formats.Avif.Encoder.Av1.Transform;

/// <summary>
/// Computes <c>output = matrix * input</c> for a <c>size</c> x <c>size</c> dense matrix and a
/// <c>size</c>-length vector -- <see cref="Av1ForwardTransform"/>'s row/column separable-transform pass,
/// called twice per block (once per axis) for every 4x4/8x8/16x16/32x32 forward DCT. Narrower than a full
/// <c>IXxxKernel</c> surface (JPEG's DCT/color-conversion precedents) because <c>ApplyMatrix</c> is a small,
/// self-contained dense matrix-vector multiply rather than a whole encode stage -- one method is all there
/// is to tier.
/// </summary>
internal interface IAv1MatrixVectorKernel
{
    /// <summary>
    /// Writes <paramref name="matrix"/> (row-major, <paramref name="size"/> x <paramref name="size"/>)
    /// times <paramref name="input"/> (length <paramref name="size"/>) into <paramref name="output"/>
    /// (length <paramref name="size"/>).
    /// </summary>
    void Apply(double[,] matrix, ReadOnlySpan<double> input, Span<double> output, int size);
}
