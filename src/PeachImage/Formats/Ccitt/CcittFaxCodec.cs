using PeachImage.Formats.Shared.Ccitt;
using PeachImage.Formats.Shared.IO;

namespace PeachImage.Formats.Ccitt;

/// <summary>
/// The raw CCITT Group 3 / Group 4 fax codec. Raw fax data has no signature, so this codec never matches by sniffing; it
/// is selected by passing <see cref="CcittFaxDecoderOptions"/>. Decode-only.
/// </summary>
internal sealed class CcittFaxCodec : IImageCodec
{
    private const long MaxInputLength = 512L * 1024 * 1024;

    private CcittFaxCodec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new CcittFaxCodec();

    /// <inheritdoc/>
    public string FormatName => "ccitt";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = [];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/g3fax"];

    /// <inheritdoc/>
    public int HeaderSize => 0;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) => false;

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
        using var image = Decode(stream, options);
        return new ImageInfo(image.Width, image.Height, PixelFormat.Gray8, FormatName, IsLosslessEncoding: true);
    }

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var fax = options as CcittFaxDecoderOptions ?? new CcittFaxDecoderOptions();

        using var input = PooledStreamBuffer.Read(stream, MaxInputLength, out bool tooLarge);
        if (tooLarge)
        {
            throw new CcittFaxDecodingException("CCITT data exceeds the supported maximum size.");
        }

        var parameters = new CcittParameters
        {
            K = fax.K,
            Columns = fax.Columns,
            EndOfLine = fax.EndOfLine,
            EncodedByteAlign = fax.EncodedByteAlign,
            EndOfBlock = fax.EndOfBlock,
            DamagedRowsBeforeError = fax.DamagedRowsBeforeError,
            LsbFirst = fax.LsbFirst,
        };

        CcittResult result;
        try
        {
            result = CcittDecoder.Decode(input.Span, parameters, fax.Rows);
        }
        catch (CcittDataException ex)
        {
            throw new CcittFaxDecodingException(ex.Message, ex);
        }
        catch (CcittUnsupportedException ex)
        {
            throw new CcittFaxUnsupportedFeatureException(ex.Message, ex);
        }

        if (result.Rows == 0)
        {
            throw new CcittFaxDecodingException("CCITT data contains no rows.");
        }

        var image = Image.Create(fax.Columns, result.Rows, PixelFormat.Gray8);
        for (int y = 0; y < result.Rows; y++)
        {
            BilevelExpander.ExpandRow(result.Bits.AsSpan(y * result.Stride, result.Stride), image.GetRowSpan(y), fax.Columns, invert: !fax.BlackIs1);
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
        throw new NotSupportedException("CCITT encoding is not supported; only decoding.");
}
