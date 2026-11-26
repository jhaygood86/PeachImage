namespace PeachImage.Formats.Webp;

/// <summary>
/// WebP-specific encode options. Defaults to lossless (VP8L) encoding, matching this encoder's original
/// lossless-only behavior; set <see cref="Lossless"/> to <see langword="false"/> for lossy VP8 encoding.
/// </summary>
public sealed class WebpEncoderOptions : EncoderOptions
{
    /// <summary>The speed/size tradeoff for lossless encoding. Defaults to <see cref="WebpCompressionLevel.Default"/>. Ignored when <see cref="Lossless"/> is <see langword="false"/>.</summary>
    public WebpCompressionLevel CompressionLevel { get; init; } = WebpCompressionLevel.Default;

    /// <summary>Whether the encoder may use a VP8L color cache to shrink repeated colors. Defaults to <see langword="true"/>. Ignored when <see cref="Lossless"/> is <see langword="false"/>.</summary>
    public bool UseColorCache { get; init; } = true;

    /// <summary>
    /// When <see langword="true"/> (the default), encodes as lossless VP8L, preserving every pixel exactly.
    /// When <see langword="false"/>, encodes as lossy VP8 using <see cref="Quality"/> -- except for images that
    /// need a real alpha channel (VP8 lossy has no native alpha plane; see
    /// <see href="https://github.com/jhaygood86/PeachImage/issues/26">issue #26</see> for the planned
    /// <c>ALPH</c>-chunk follow-on), which are still encoded as VP8L regardless of this setting so that
    /// <see langword="false"/> never silently loses alpha.
    /// </summary>
    public bool Lossless { get; init; } = true;

    /// <summary>
    /// Lossy quality, 0 (smallest/worst) to 100 (largest/best), matching <see cref="Jpeg.JpegEncoderOptions.Quality"/>'s
    /// scale and default. Ignored when <see cref="Lossless"/> is <see langword="true"/>. There is deliberately
    /// no target-file-size control yet, for the same reason <see cref="Jpeg.JpegEncoderOptions"/> has none --
    /// that needs either a real two-pass encode or an iterative rate model, neither of which this v1 lossy
    /// encoder implements.
    /// </summary>
    public int Quality { get; init; } = 75;

    /// <summary>
    /// <see cref="Quality"/> as a fraction from 0 to 1, mapped the way a browser maps the <c>quality</c> argument of <c>canvas.toBlob(type, quality)</c>:
    /// <c>Quality = round(value x 100)</c>. A value of 1 also selects lossless encoding (<see cref="Lossless"/>), as Chromium does; anything lower selects lossy.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is NaN or outside 0..1 (a browser would use its default; pass that instead).</exception>
    public float QualityFraction
    {
        get => Quality / 100f;
        init
        {
            Quality = PeachImage.Internal.BrowserQuality.ToPercent(value);
            Lossless = value >= 1f;
        }
    }
}
