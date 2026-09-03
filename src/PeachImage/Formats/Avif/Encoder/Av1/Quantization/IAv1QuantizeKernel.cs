namespace PeachImage.Formats.Avif.Encoder.Av1.Quantization;

/// <summary>
/// Reciprocal-multiply, SIMD-tiered core of <see cref="Av1ForwardQuantizer.Quantize"/>: multiplies every
/// coefficient by a precomputed reciprocal (instead of dividing per-coefficient) and rounds away from zero.
/// </summary>
internal interface IAv1QuantizeKernel
{
    /// <summary>
    /// Quantizes <paramref name="coeff"/> (<paramref name="size"/> x <paramref name="size"/>, row-major,
    /// flat index 0 is the DC coefficient) into <paramref name="levelsOut"/>: index 0 is multiplied by
    /// <paramref name="dcReciprocal"/>, every other index by <paramref name="acReciprocal"/>, both then
    /// rounded away from zero (matching <see cref="Av1ForwardQuantizer"/>'s original
    /// <see cref="MidpointRounding.AwayFromZero"/> convention exactly).
    /// </summary>
    void Quantize(ReadOnlySpan<int> coeff, Span<int> levelsOut, int size, double dcReciprocal, double acReciprocal);
}
