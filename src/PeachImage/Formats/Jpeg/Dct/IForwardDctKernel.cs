namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>Performs a forward 8x8 DCT on level-shifted samples, producing un-quantized frequency-domain coefficients.</summary>
internal interface IForwardDctKernel
{
    /// <summary>
    /// Prepares the per-block quantization table an encoder should divide this kernel's <see cref="Transform"/>
    /// output by, from the raw (natural-order) quantization values for a component. Called once per component,
    /// not once per block. Kernels whose raw output is numerically equivalent to the direct-definition FDCT use
    /// the default implementation (the table is returned unchanged); kernels whose fast transform's raw output
    /// carries per-frequency scale factors (e.g. AAN) override this to fold them into the divisor once here.
    /// </summary>
    double[] PrepareQuantTable(ReadOnlySpan<ushort> quantTable)
    {
        var widened = new double[64];
        for (int i = 0; i < 64; i++)
        {
            widened[i] = quantTable[i];
        }

        return widened;
    }

    /// <summary>
    /// Transforms one 8x8 block: <paramref name="input"/> (8x8 bytes, row stride <paramref name="inputStride"/>)
    /// is level-shifted by -128 and forward-DCT'd, writing 64 coefficients (natural order) to <paramref name="output"/>.
    /// </summary>
    void Transform(ReadOnlySpan<byte> input, int inputStride, Span<double> output);
}
