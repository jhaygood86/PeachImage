namespace PeachImage.Formats.Gif;

/// <summary>The GIF codec: bundles <see cref="GifDecoder"/> and <see cref="GifEncoder"/> for registration with <see cref="ImageFormatManager"/>.</summary>
public sealed class GifCodec : IImageCodec
{
    private GifCodec()
    {
    }

    /// <summary>The shared codec instance, automatically registered as a built-in codec by <see cref="ImageFormatManager"/>.</summary>
    public static IImageCodec Instance { get; } = new GifCodec();

    /// <inheritdoc/>
    public string FormatName => "gif";

    /// <inheritdoc/>
    public IImageDecoder? Decoder { get; } = new GifDecoder();

    /// <inheritdoc/>
    public IImageEncoder? Encoder { get; } = new GifEncoder();
}
