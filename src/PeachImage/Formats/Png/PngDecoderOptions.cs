namespace PeachImage.Formats.Png;

/// <summary>PNG-specific decode options.</summary>
public sealed class PngDecoderOptions : DecoderOptions
{
    /// <summary>
    /// The viewing display's gamma, for optional gamma correction — mirrors libpng's
    /// <c>png_set_gamma(screen_gamma, file_gamma)</c> model. When <see langword="null"/> (the default),
    /// no gamma correction is applied and decoded samples are returned exactly as encoded, regardless of
    /// any <c>gAMA</c>/<c>sRGB</c> chunk present. When set, and the source declares a file gamma (via
    /// <c>gAMA</c>, or implicitly 1/2.2 via <c>sRGB</c>), color/gray samples are corrected as
    /// <c>output = round(max * (input / max) ^ (fileGamma / screenGamma))</c>. Alpha is never corrected.
    /// A typical value for a conventional sRGB-ish display is <c>2.2</c>.
    /// </summary>
    public double? ScreenGamma { get; init; }
}
