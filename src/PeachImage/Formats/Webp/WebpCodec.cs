namespace PeachImage.Formats.Webp;

/// <summary>The WebP codec: bundles <see cref="WebpDecoder"/> for registration with <see cref="ImageFormatManager"/>. Decode-only for now — WebP encoding is planned but not yet implemented.</summary>
public sealed class WebpCodec : IImageCodec
{
    private WebpCodec()
    {
    }

    /// <summary>The shared codec instance, automatically registered as a built-in codec by <see cref="ImageFormatManager"/>.</summary>
    public static IImageCodec Instance { get; } = new WebpCodec();

    /// <inheritdoc/>
    public string FormatName => "webp";

    /// <inheritdoc/>
    public IImageDecoder? Decoder { get; } = new WebpDecoder();

    /// <inheritdoc/>
    public IImageEncoder? Encoder => null;
}
