using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// Quantizes a 4x4 block of forward-DCT coefficients (natural order, as <see cref="Vp8ForwardDct"/> or
/// <see cref="Vp8ForwardWht"/> produce them) into zigzag-scan-order quantized levels, and the reverse
/// (dequantizes zigzag levels back into a natural-order coefficient block) for the encoder's own reconstruction
/// pass. Reuses <see cref="Decoding.Vp8.Vp8QuantMatrix"/>/<see cref="Vp8Dequantizer"/> as-is — quantization is
/// symmetric, so the same per-segment DC/AC step sizes the decoder resolves apply directly to encode.
/// </summary>
internal static class Vp8ForwardQuantizer
{
    /// <summary>
    /// Quantizes <paramref name="coefficients"/> (natural raster order) into <paramref name="quantized"/>
    /// (zigzag scan order, integer levels — not yet multiplied back up by the quant step). Returns the scan
    /// position one past the last nonzero level (0 if the block is entirely zero, 16 if position 15 is
    /// nonzero) — the encode-side mirror of <see cref="Vp8CoefficientDecoder.DecodeBlock"/>'s <c>last</c>
    /// bookkeeping, which the coefficient token encoder needs to know where to stop.
    /// </summary>
    public static int Quantize(ReadOnlySpan<short> coefficients, int dcQuant, int acQuant, Span<short> quantized)
    {
        int last = 0;
        for (int scan = 0; scan < 16; scan++)
        {
            int naturalPos = Vp8ZigZag.Order[scan];
            int coeff = coefficients[naturalPos];
            int quant = scan == 0 ? dcQuant : acQuant;
            int level = QuantizeOne(coeff, quant);
            quantized[scan] = (short)level;
            if (level != 0)
            {
                last = scan + 1;
            }
        }

        return last;
    }

    /// <summary>
    /// Dequantizes <paramref name="quantized"/> (zigzag scan order levels) back into
    /// <paramref name="output"/> (natural raster order, each level multiplied by its DC/AC quant step) — the
    /// same natural-order/dequantized-value layout <see cref="Vp8CoefficientDecoder.DecodeBlock"/> produces,
    /// so <see cref="Decoding.Vp8.Dct.Vp8ScalarInverseDct"/>/<see cref="Decoding.Vp8.Dct.Vp8ScalarInverseWht"/>
    /// can consume it unchanged for the encoder's own reconstruction pass.
    /// </summary>
    public static void Dequantize(ReadOnlySpan<short> quantized, int dcQuant, int acQuant, Span<short> output)
    {
        output.Clear();
        for (int scan = 0; scan < 16; scan++)
        {
            int level = quantized[scan];
            if (level == 0)
            {
                continue;
            }

            int naturalPos = Vp8ZigZag.Order[scan];
            int quant = scan == 0 ? dcQuant : acQuant;
            output[naturalPos] = (short)(level * quant);
        }
    }

    /// <summary>Round-to-nearest, sign-aware quantization of a single coefficient — magnitude divided by <paramref name="quant"/> with a half-step rounding bias, sign reapplied afterward.</summary>
    private static int QuantizeOne(int coeff, int quant)
    {
        int magnitude = Math.Abs(coeff);
        int level = (magnitude + (quant / 2)) / quant;
        return coeff < 0 ? -level : level;
    }
}
