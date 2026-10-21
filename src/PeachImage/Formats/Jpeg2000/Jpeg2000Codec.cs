using PeachImage.Formats.Jpeg2000.Decoding;

namespace PeachImage.Formats.Jpeg2000;

/// <summary>
/// The JPEG 2000 codec (ITU-T T.800 Part 1): JP2 files and raw codestreams. Format identity and header-sniffing live here,
/// the single <see cref="IImageCodec"/> surface <see cref="Image"/> dispatches through. Decode-only.
/// </summary>
internal sealed class Jpeg2000Codec : IImageCodec
{
    private Jpeg2000Codec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new Jpeg2000Codec();

    /// <inheritdoc/>
    public string FormatName => "jpeg2000";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["jp2", "j2k", "j2c", "jpc", "jpx"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/jp2", "image/j2k"];

    /// <inheritdoc/>
    public int HeaderSize => 12;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) =>
        Jp2Info.HasJp2Signature(header) || Jp2Info.HasCodestreamSignature(header);

    /// <inheritdoc/>
    public bool CanDecode => true;

    /// <inheritdoc/>
    public bool CanEncode => false;

    /// <inheritdoc/>
    public bool CanDecodeTransparency => true;

    /// <inheritdoc/>
    public bool CanEncodeTransparency => false;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream) => Jpeg2000Decoder.Identify(stream, null);

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream, DecoderOptions? options) => Jpeg2000Decoder.Identify(stream, options);

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null) => Jpeg2000Decoder.Decode(stream, options);

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) =>
        throw new NotSupportedException("JPEG 2000 encoding is not supported; only decoding.");
}
