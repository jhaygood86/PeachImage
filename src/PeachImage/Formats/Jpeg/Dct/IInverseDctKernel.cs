namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>Performs dequantization plus an 8x8 inverse DCT, producing level-shifted-back (0-255) byte samples.</summary>
internal interface IInverseDctKernel
{
    /// <summary>
    /// Transforms one 8x8 block: <paramref name="coefficients"/> (64 values, natural order) are multiplied by
    /// <paramref name="dequantTable"/> (64 values, natural order), inverse-DCT'd, and written to
    /// <paramref name="output"/> as a 8x8 block of bytes with row stride <paramref name="outputStride"/>.
    /// </summary>
    void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> dequantTable, Span<byte> output, int outputStride);
}
