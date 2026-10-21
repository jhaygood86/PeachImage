using System.Numerics;
using PeachImage.Formats.Jbig2.Decoding;
using PeachImage.Formats.Jbig2.Internal;
using PeachImage.Formats.Shared.IO;

namespace PeachImage.Formats.Jbig2;

/// <summary>
/// The JBIG2 codec (ITU-T T.88): standalone files, recognized by their signature, and — selected through
/// <see cref="Jbig2DecoderOptions.Embedded"/> — the segment streams embedded in PDF files. Decode-only; the first page is decoded.
/// </summary>
internal sealed class Jbig2Codec : IImageCodec
{
    private Jbig2Codec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new Jbig2Codec();

    /// <inheritdoc/>
    public string FormatName => "jbig2";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["jb2", "jbig2"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/x-jbig2"];

    /// <inheritdoc/>
    public int HeaderSize => 8;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) => Jbig2Document.HasFileSignature(header);

    /// <inheritdoc/>
    public bool CanDecode => true;

    /// <inheritdoc/>
    public bool CanEncode => false;

    /// <inheritdoc/>
    public bool CanDecodeTransparency => false;

    /// <inheritdoc/>
    public bool CanEncodeTransparency => false;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream) => Identify(stream, null);

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream, DecoderOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var jbig2 = options as Jbig2DecoderOptions;
        bool embedded = jbig2?.Embedded ?? false;

        using var input = PooledStreamBuffer.Read(stream, Jbig2DecodingLimits.MaxFileSize, out bool tooLarge);
        if (tooLarge)
        {
            throw new Jbig2DecodingException("JBIG2 data exceeds the supported maximum size.");
        }

        var (width, height) = Jbig2Document.ReadPageSize(input.ToMemory(), embedded);
        if (height == 0)
        {
            height = Jbig2Document.Decode(input.ToMemory(), jbig2?.Globals ?? default, embedded).Height;
        }

        return new ImageInfo(width, height, PixelFormat.Gray8, FormatName, IsLosslessEncoding: true);
    }

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var jbig2 = options as Jbig2DecoderOptions;
        bool embedded = jbig2?.Embedded ?? false;

        using var input = PooledStreamBuffer.Read(stream, Jbig2DecodingLimits.MaxFileSize, out bool tooLarge);
        if (tooLarge)
        {
            throw new Jbig2DecodingException("JBIG2 data exceeds the supported maximum size.");
        }

        Jbig2Bitmap page;
        try
        {
            page = Jbig2Document.Decode(input.ToMemory(), jbig2?.Globals ?? default, embedded);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new Jbig2DecodingException("JBIG2 data is malformed.", ex);
        }

        if (page.Width == 0 || page.Height == 0)
        {
            throw new Jbig2DecodingException("JBIG2 page is empty.");
        }

        var image = Image.Create(page.Width, page.Height, PixelFormat.Gray8);
        bool invert = jbig2?.InvertOutput ?? false;
        for (int y = 0; y < page.Height; y++)
        {
            ToGray(page.Row(y), image.GetRowSpan(y), invert);
        }

        if (options?.TargetPixelFormat is { } target && target != PixelFormat.Gray8)
        {
            using (image)
            {
                return image.ConvertTo(target);
            }
        }

        return image;
    }

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) =>
        throw new NotSupportedException("JBIG2 encoding is not supported; only decoding.");

    // Black (1) becomes 0 and white (0) becomes 255, or the reverse when inverted: (1 - v) * 255 == 0 - (v ^ 1).
    private static void ToGray(ReadOnlySpan<byte> source, Span<byte> destination, bool invert)
    {
        int i = 0;
        int lanes = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated && source.Length >= lanes)
        {
            var one = Vector<byte>.One;
            for (; i <= source.Length - lanes; i += lanes)
            {
                var v = new Vector<byte>(source.Slice(i));
                var white = invert ? Vector<byte>.Zero - v : Vector<byte>.Zero - (v ^ one);
                white.CopyTo(destination.Slice(i));
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] = invert ? (byte)(0 - source[i]) : (byte)(0 - (source[i] ^ 1));
        }
    }
}
