namespace PeachImage.Formats.Png.Internal;

/// <summary>
/// Builds the lookup table mapping a raw, unscaled PNG sample (grayscale/truecolor color-type
/// channels only — never palette indices, never alpha) to its final output channel value: for bit
/// depths below 8, this both scales to the 0-255 display range (depth 1: {0,255}; depth 2:
/// {0,85,170,255}; depth 4: 17-multiples) and, optionally, applies gamma correction; for depths 8/16
/// it is scale-neutral (identity when no gamma is requested) and applies gamma correction only.
/// tRNS-key comparison must always happen against the raw sample, before this table is consulted
/// (spec §11.3.2.1 defines the key in the same unscaled range as the sample).
/// </summary>
internal static class PngSampleLut
{
    public static ushort[] Build(int bitDepth, double? fileGamma, double? screenGamma)
    {
        int inputMax = (1 << bitDepth) - 1;
        int outputMax = bitDepth <= 8 ? 255 : 65535;
        var lut = new ushort[inputMax + 1];

        double? gammaExponent = fileGamma is { } fg && screenGamma is > 0 ? fg / screenGamma : null;

        for (int i = 0; i <= inputMax; i++)
        {
            double normalized = (double)i / inputMax;
            double corrected = gammaExponent is { } exponent ? Math.Pow(normalized, exponent) : normalized;
            lut[i] = (ushort)Math.Clamp(Math.Round(corrected * outputMax, MidpointRounding.AwayFromZero), 0, outputMax);
        }

        return lut;
    }
}
