using PeachImage.Formats.Jxl.Container;

namespace PeachImage.Formats.Jxl;

/// <summary>
/// The JPEG XL codec. Format identity and header-sniffing live here, the single <see cref="IImageCodec"/>
/// surface <see cref="Image"/> dispatches through; decode is a separate internal implementation detail
/// (<see cref="JxlDecoder"/>). Decode-only — see <see cref="Encode"/>.
/// </summary>
internal sealed class JxlCodec : IAnimatedImageCodec
{
    private JxlCodec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new JxlCodec();

    /// <inheritdoc/>
    public string FormatName => "jxl";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["jxl"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/jxl"];

    /// <inheritdoc/>
    public int HeaderSize => 12;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) => JxlContainer.HasSignature(header);

    /// <inheritdoc/>
    public bool CanDecode => true;

    /// <inheritdoc/>
    public bool CanEncode => false;

    /// <inheritdoc/>
    public bool CanDecodeTransparency => true;

    /// <inheritdoc/>
    public bool CanEncodeTransparency => false;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream) => JxlDecoder.Identify(stream);

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null) => JxlDecoder.Decode(stream, options);

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) =>
        throw new NotSupportedException("JPEG XL encoding is not supported; only decoding.");

    /// <inheritdoc/>
    public bool CanEncodeAnimation => false;

    /// <inheritdoc/>
    public AnimatedImage DecodeAnimation(Stream stream, DecoderOptions? options = null) => JxlDecoder.DecodeAnimation(stream, options);

    /// <inheritdoc/>
    public void EncodeAnimation(AnimatedImage image, Stream stream, EncoderOptions? options = null) =>
        throw new NotSupportedException("JPEG XL encoding is not supported; only decoding.");
}
