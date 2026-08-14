namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// The AAN (Arai-Agui-Nakajima) fast-DCT algorithm's per-frequency scale factors. AAN's fast 1D butterfly
/// network produces each output frequency <c>u</c> scaled by a fixed constant <c>S(u)</c> relative to the
/// true DCT coefficient, in exchange for needing far fewer multiplies than the direct separable-transform
/// definition. Folding <c>S(u)*S(v)</c> into the (de)quantization table once per component — rather than
/// correcting for it after every block's transform — lets an AAN kernel consume/produce coefficients that
/// are numerically equivalent to <see cref="ScalarInverseDct"/>/<see cref="ScalarForwardDct"/>'s output.
/// </summary>
internal static class AanScaleFactors
{
    /// <summary>
    /// <c>OneDimensional[u]</c> is the AAN scale factor for frequency <c>u</c>: <c>1</c> at <c>u=0</c> and
    /// <c>u=4</c> (the butterfly network needs no correction there), and <c>sqrt(2) * cos(u*pi/16)</c> for
    /// the remaining odd/off-axis frequencies.
    /// </summary>
    public static ReadOnlySpan<double> OneDimensional =>
    [
        1.0,
        1.3870398453221475,
        1.3065629648763766,
        1.1758756024193588,
        1.0,
        0.7856949583871023,
        0.5411961001461971,
        0.2758993792829431,
    ];

    /// <summary>
    /// Builds a dequantization table (natural order, 64 entries) for an AAN inverse-DCT kernel: each entry
    /// is <paramref name="dequantTable"/>'s value scaled by <c>S(u)*S(v)</c>, so that feeding AAN's fast
    /// IDCT butterfly network the result — instead of the raw dequant table — yields spatial output
    /// numerically equivalent to the direct-definition inverse DCT.
    /// </summary>
    public static float[] BuildInverseDequantTable(ReadOnlySpan<ushort> dequantTable)
    {
        var scaled = new float[64];
        var s = OneDimensional;
        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                scaled[(v * 8) + u] = (float)(dequantTable[(v * 8) + u] * s[u] * s[v]);
            }
        }

        return scaled;
    }

    /// <summary>
    /// Builds a quantization table (natural order, 64 entries) for an AAN forward-DCT kernel: each entry is
    /// <paramref name="quantTable"/>'s value scaled by <c>S(u)*S(v)</c>, so that dividing AAN's fast FDCT
    /// butterfly network's raw output by the result — instead of by the raw quant table — yields quantized
    /// coefficients numerically equivalent to the direct-definition forward DCT.
    /// </summary>
    public static double[] BuildForwardQuantTable(ReadOnlySpan<ushort> quantTable)
    {
        var scaled = new double[64];
        var s = OneDimensional;
        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                scaled[(v * 8) + u] = quantTable[(v * 8) + u] * s[u] * s[v];
            }
        }

        return scaled;
    }
}
