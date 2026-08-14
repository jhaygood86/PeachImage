namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>Performs dequantization plus an 8x8 inverse DCT, producing level-shifted-back (0-255) byte samples.</summary>
internal interface IInverseDctKernel
{
    /// <summary>
    /// Prepares the per-block dequantization table this kernel expects <see cref="Transform"/> to receive,
    /// from the raw (natural-order) dequantization values for a component. Called once per component, not
    /// once per block. Kernels that consume the dequant table as-is (the direct dot-product tiers) use the
    /// default implementation; kernels whose fast transform needs per-frequency scale factors folded into
    /// the table (e.g. AAN) override this to fold them in once here rather than on every block.
    /// </summary>
    float[] PrepareDequantTable(ReadOnlySpan<ushort> dequantTable)
    {
        var widened = new float[64];
        for (int i = 0; i < 64; i++)
        {
            widened[i] = dequantTable[i];
        }

        return widened;
    }

    /// <summary>
    /// Transforms one 8x8 block: <paramref name="coefficients"/> (64 values, natural order) are multiplied by
    /// <paramref name="dequantTable"/> (64 values, natural order, as produced by <see cref="PrepareDequantTable"/>),
    /// inverse-DCT'd, and written to <paramref name="output"/> as a 8x8 block of bytes with row stride
    /// <paramref name="outputStride"/>.
    /// </summary>
    void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<float> dequantTable, Span<byte> output, int outputStride);
}
