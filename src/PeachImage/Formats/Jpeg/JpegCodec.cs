namespace PeachImage.Formats.Jpeg;

/// <summary>
/// The JPEG codec. Format identity and header-sniffing live here, the single <see cref="IImageCodec"/>
/// surface <see cref="Image"/> dispatches through; decode and encode are separate internal
/// implementation details (<see cref="JpegDecoder"/>/<see cref="JpegEncoder"/>) composed privately
/// rather than exposed as their own abstractions.
/// </summary>
internal sealed class JpegCodec : IImageCodec
{
    private JpegCodec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new JpegCodec();

    /// <inheritdoc/>
    public string FormatName => "jpeg";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["jpg", "jpeg", "jpe", "jfif"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/jpeg"];

    /// <inheritdoc/>
    public int HeaderSize => 3;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) =>
        header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

    /// <inheritdoc/>
    public bool CanDecode => true;

    /// <inheritdoc/>
    public bool CanEncode => true;

    /// <inheritdoc/>
    public bool CanDecodeTransparency => false;

    /// <inheritdoc/>
    public bool CanEncodeTransparency => false;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream) => JpegDecoder.Identify(stream);

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null) => JpegDecoder.Decode(stream, options);

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) => JpegEncoder.Encode(image, stream, options);
}
