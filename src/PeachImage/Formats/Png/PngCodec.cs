namespace PeachImage.Formats.Png;

/// <summary>The PNG codec: bundles <see cref="PngDecoder"/> and <see cref="PngEncoder"/> for registration with <see cref="ImageFormatManager"/>.</summary>
public sealed class PngCodec : IImageCodec
{
    private PngCodec()
    {
    }

    /// <summary>The shared codec instance, automatically registered as a built-in codec by <see cref="ImageFormatManager"/>.</summary>
    public static IImageCodec Instance { get; } = new PngCodec();

    /// <inheritdoc/>
    public string FormatName => "png";

    /// <inheritdoc/>
    public IImageDecoder? Decoder { get; } = new PngDecoder();

    /// <inheritdoc/>
    public IImageEncoder? Encoder { get; } = new PngEncoder();
}
