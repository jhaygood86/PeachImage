namespace PeachImage.Formats.Webp;

/// <summary>
/// WebP-specific encode options. This encoder only ever produces the lossless (VP8L) bitstream in this
/// version -- there is deliberately no <c>Lossless</c>/<c>Quality</c> toggle yet, since a silently-ignored
/// quality knob would be misleading. Adding a lossy mode later is a non-breaking new <see langword="init"/>
/// property whenever VP8 encode support lands.
/// </summary>
public sealed class WebpEncoderOptions : EncoderOptions
{
    /// <summary>The speed/size tradeoff for lossless encoding. Defaults to <see cref="WebpCompressionLevel.Default"/>.</summary>
    public WebpCompressionLevel CompressionLevel { get; init; } = WebpCompressionLevel.Default;

    /// <summary>Whether the encoder may use a VP8L color cache to shrink repeated colors. Defaults to <see langword="true"/>.</summary>
    public bool UseColorCache { get; init; } = true;
}
