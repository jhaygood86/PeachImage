namespace PeachImage.Formats.Jpeg;

/// <summary>The JPEG codec: bundles <see cref="JpegDecoder"/> and <see cref="JpegEncoder"/> for registration with <see cref="ImageFormatManager"/>.</summary>
public sealed class JpegCodec : IImageCodec
{
    private JpegCodec()
    {
    }

    /// <summary>The shared codec instance, automatically registered as a built-in codec by <see cref="ImageFormatManager"/>.</summary>
    public static IImageCodec Instance { get; } = new JpegCodec();

    /// <inheritdoc/>
    public string FormatName => "jpeg";

    /// <inheritdoc/>
    public IImageDecoder? Decoder { get; } = new JpegDecoder();

    /// <inheritdoc/>
    public IImageEncoder? Encoder { get; } = new JpegEncoder();
}
