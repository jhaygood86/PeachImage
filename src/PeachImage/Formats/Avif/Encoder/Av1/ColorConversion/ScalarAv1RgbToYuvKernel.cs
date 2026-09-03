namespace PeachImage.Formats.Avif.Encoder.Av1.ColorConversion;

/// <summary>
/// Reference (always-correct, not performance-optimized) RGB24-&gt;YUV kernel. See
/// <see cref="Av1RgbToYuvCoefficients"/> for the shared float-precision BT.601 math this and the SIMD
/// kernels compute, and <see cref="ClampToByte"/> for the rounding convention all three tiers share.
/// </summary>
internal sealed class ScalarAv1RgbToYuvKernel : IAv1RgbToYuvKernel
{
    public void ConvertMonoChrome(ReadOnlySpan<byte> gray, Span<int> yOut, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            yOut[i] = gray[i];
        }
    }

    public void RgbToYuvFullRes(ReadOnlySpan<byte> rgb, Span<int> yOut, Span<float> cbFullOut, Span<float> crFullOut, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            ConvertPixel(rgb, i, yOut, cbFullOut, crFullOut);
        }
    }

    /// <summary>Converts one RGB24 pixel at <paramref name="i"/> -- factored out so the SIMD kernels can reuse it verbatim for their scalar remainder tail (pixel counts not evenly divisible by the vector width).</summary>
    internal static void ConvertPixel(ReadOnlySpan<byte> rgb, int i, Span<int> yOut, Span<float> cbFullOut, Span<float> crFullOut)
    {
        int srcIdx = i * 3;
        float r = rgb[srcIdx];
        float g = rgb[srcIdx + 1];
        float b = rgb[srcIdx + 2];

        float y = (Av1RgbToYuvCoefficients.Kr * r) + (Av1RgbToYuvCoefficients.Kg * g) + (Av1RgbToYuvCoefficients.Kb * b);
        float cb = 128f + ((b - y) * Av1RgbToYuvCoefficients.KbInvHalf);
        float cr = 128f + ((r - y) * Av1RgbToYuvCoefficients.KrInvHalf);

        yOut[i] = ClampToByte(y);
        cbFullOut[i] = cb;
        crFullOut[i] = cr;
    }

    /// <summary>
    /// Clamps to [0, 255] then rounds half-up: every value reaching here is clamped non-negative first, so
    /// "add 0.5 and truncate toward zero" is exact round-half-up with no negative-value edge case -- the
    /// same bias-and-truncate trick <see cref="Jpeg.ColorConversion.Vector256ColorConverter"/> uses, kept
    /// identical across all three tiers (rather than this scalar tier using <see cref="MathF.Round(float)"/>'s
    /// default round-half-to-even) so the kernel-agreement tests can assert exact equality on the Y channel,
    /// not just a tolerance.
    /// </summary>
    internal static int ClampToByte(float value)
    {
        float clamped = Math.Clamp(value, 0f, 255f);
        return (int)(clamped + 0.5f);
    }
}
