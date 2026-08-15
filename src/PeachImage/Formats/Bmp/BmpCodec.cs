namespace PeachImage.Formats.Bmp;

/// <summary>The BMP codec: bundles <see cref="BmpDecoder"/> and <see cref="BmpEncoder"/> for registration with <see cref="ImageFormatManager"/>.</summary>
public sealed class BmpCodec : IImageCodec
{
    private BmpCodec()
    {
    }

    /// <summary>The shared codec instance, automatically registered as a built-in codec by <see cref="ImageFormatManager"/>.</summary>
    public static IImageCodec Instance { get; } = new BmpCodec();

    /// <inheritdoc/>
    public string FormatName => "bmp";

    /// <inheritdoc/>
    public IImageDecoder? Decoder { get; } = new BmpDecoder();

    /// <inheritdoc/>
    public IImageEncoder? Encoder { get; } = new BmpEncoder();
}
