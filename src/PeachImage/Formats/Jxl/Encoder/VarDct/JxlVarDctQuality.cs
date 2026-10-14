namespace PeachImage.Formats.Jxl.Encoder.VarDct;

/// <summary>
/// Maps the public encoder options onto the quantization settings of a VarDCT frame. The constants were tuned for the smallest
/// files at equal SSIMULACRA2 score on a spread of photographs.
/// </summary>
internal static class JxlVarDctQuality
{
    public static JxlVarDctSettings Settings(JxlEncoderOptions options)
    {
        int quality = options.Quality;

        // libjxl's quality-to-distance curve: 90 is distance 1 (visually lossless), 75 is about 2.3.
        float distance = quality >= 30
            ? 0.1f + ((100 - quality) * 0.09f)
            : (53f / 3000f * quality * quality) - (23f / 20f * quality) + 25f;

        return new JxlVarDctSettings(
            AcScale: distance * 0.75f,

            // The DC image is coded cheaply and carries most of the large-scale error, so it is quantized much more finely than the AC.
            QuantDc: 192,
            Deadzone: 0.05f,

            // Pre-sharpening for the decoder's Gaborish smoothing only paid off in the quality range where blocking, not detail loss, dominates.
            Gaborish: quality is >= 80 and <= 90,
            EpfIterations: 1,
            EpfSharpness: 7,
            Effort: options.Effort,
            AdaptiveQuantization: options.AdaptiveQuantization ? 1f : 0f);
    }
}
