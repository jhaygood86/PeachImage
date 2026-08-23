namespace PeachImage.Formats.Tiff;

/// <summary>
/// The TIFF codec. Format identity and header-sniffing live here, the single <see cref="IImageCodec"/>
/// surface <see cref="Image"/> dispatches through; decode is a separate internal implementation detail
/// (<see cref="TiffDecoder"/>) composed privately rather than exposed as its own abstraction. Decode-only —
/// see <see cref="Encode"/>.
/// </summary>
internal sealed class TiffCodec : IImageCodec
{
    private TiffCodec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new TiffCodec();

    /// <inheritdoc/>
    public string FormatName => "tiff";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["tiff", "tif"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/tiff"];

    /// <inheritdoc/>
    public int HeaderSize => 4;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) =>
        header.Length >= 4 &&
        ((header[0] == (byte)'I' && header[1] == (byte)'I' && header[2] == 0x2A && header[3] == 0x00) ||
         (header[0] == (byte)'M' && header[1] == (byte)'M' && header[2] == 0x00 && header[3] == 0x2A));

    /// <inheritdoc/>
    public bool CanDecode => true;

    /// <inheritdoc/>
    public bool CanEncode => false;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream) => TiffDecoder.Identify(stream);

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null) => TiffDecoder.Decode(stream, options);

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) =>
        throw new NotSupportedException("TIFF encoding is not supported; only decoding.");
}
