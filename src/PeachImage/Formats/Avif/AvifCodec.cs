namespace PeachImage.Formats.Avif;

/// <summary>
/// The AVIF codec. Format identity and header-sniffing live here, the single <see cref="IImageCodec"/>
/// surface <see cref="Image"/> dispatches through; decode and encode are separate internal implementation
/// details (<see cref="AvifDecoder"/>/<see cref="AvifEncoder"/>) composed privately rather than exposed as
/// their own abstraction. Encode produces a lossy, 8-bit, 4:2:0, single still-image item (no animation, no
/// alpha, no HEIF grid) — decode remains the fuller-featured side (10-bit, alpha, grid).
/// </summary>
internal sealed class AvifCodec : IImageCodec
{
    private AvifCodec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new AvifCodec();

    /// <inheritdoc/>
    public string FormatName => "avif";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["avif"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/avif"];

    /// <inheritdoc/>
    public int HeaderSize => 32;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) => AvifDecoder.IsSupportedFileFormat(header);

    /// <inheritdoc/>
    public bool CanDecode => true;

    /// <inheritdoc/>
    public bool CanEncode => true;

    /// <inheritdoc/>
    public bool CanDecodeTransparency => true;

    /// <inheritdoc/>
    public bool CanEncodeTransparency => false;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream) => AvifDecoder.Identify(stream);

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null) => AvifDecoder.Decode(stream, options);

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) => AvifEncoder.Encode(image, stream, options);
}
