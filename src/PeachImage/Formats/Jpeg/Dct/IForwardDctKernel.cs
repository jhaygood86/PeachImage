namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>Performs a forward 8x8 DCT on level-shifted samples, producing un-quantized frequency-domain coefficients.</summary>
internal interface IForwardDctKernel
{
    /// <summary>
    /// Transforms one 8x8 block: <paramref name="input"/> (8x8 bytes, row stride <paramref name="inputStride"/>)
    /// is level-shifted by -128 and forward-DCT'd, writing 64 coefficients (natural order) to <paramref name="output"/>.
    /// </summary>
    void Transform(ReadOnlySpan<byte> input, int inputStride, Span<double> output);
}
