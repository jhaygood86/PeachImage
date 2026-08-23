namespace PeachImage.Formats.Bmp;

/// <summary>
/// The BMP codec. Format identity and header-sniffing live here, the single <see cref="IImageCodec"/>
/// surface <see cref="Image"/> dispatches through; decode and encode are separate internal
/// implementation details (<see cref="BmpDecoder"/>/<see cref="BmpEncoder"/>) composed privately
/// rather than exposed as their own abstractions.
/// </summary>
internal sealed class BmpCodec : IImageCodec
{
    private BmpCodec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new BmpCodec();

    /// <inheritdoc/>
    public string FormatName => "bmp";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["bmp"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/bmp", "image/x-ms-bmp"];

    /// <inheritdoc/>
    public int HeaderSize => 2;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) =>
        header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M';

    /// <inheritdoc/>
    public bool CanDecode => true;

    /// <inheritdoc/>
    public bool CanEncode => true;

    /// <inheritdoc/>
    public bool CanDecodeTransparency => true;

    /// <inheritdoc/>
    public bool CanEncodeTransparency => true;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream) => BmpDecoder.Identify(stream);

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null) => BmpDecoder.Decode(stream, options);

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) => BmpEncoder.Encode(image, stream, options);
}
