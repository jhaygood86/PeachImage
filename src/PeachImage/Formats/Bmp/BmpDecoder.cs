using PeachImage.Formats.Bmp.Decoding;

namespace PeachImage.Formats.Bmp;

/// <summary>Decodes Windows Bitmap (BMP) images, including OS/2 header variants, 1/4/8bpp indexed color, 16/24/32bpp direct color, RLE4/RLE8 compression, and explicit alpha channels (BI_BITFIELDS/BI_ALPHABITFIELDS or BITMAPV3+ headers with a declared alpha mask).</summary>
public sealed class BmpDecoder : IImageDecoder
{
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
    public ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = BmpHeaderReader.Read(stream);
        bool hasAlpha = header.HasAlphaMask && header.BitCount is 16 or 32;
        var pixelFormat = hasAlpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;

        return new ImageInfo(header.Width, header.Height, pixelFormat, FormatName);
    }

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var image = BmpImageDecoder.Decode(stream);
        return PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
    }
}
