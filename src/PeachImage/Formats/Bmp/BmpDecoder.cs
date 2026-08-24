using PeachImage.Formats.Bmp.Decoding;

namespace PeachImage.Formats.Bmp;

/// <summary>Decodes Windows Bitmap (BMP) images, including OS/2 header variants, 1/4/8bpp indexed color, 16/24/32bpp direct color, RLE4/RLE8 compression, and explicit alpha channels (BI_BITFIELDS/BI_ALPHABITFIELDS or BITMAPV3+ headers with a declared alpha mask). Used internally by <see cref="BmpCodec"/>.</summary>
internal static class BmpDecoder
{
    private const string FormatName = "bmp";

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = BmpHeaderReader.Read(stream);
        bool hasAlpha = header.HasAlphaMask && header.BitCount is 16 or 32;
        var pixelFormat = hasAlpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;

        return new ImageInfo(header.Width, header.Height, pixelFormat, FormatName, HasAlpha: pixelFormat.HasAlpha());
    }

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>.</summary>
    public static Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var image = BmpImageDecoder.Decode(stream);
        bool hasAlpha = image.PixelFormat.HasAlpha();
        var result = PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
        if (!ReferenceEquals(result, image))
        {
            image.Dispose();
        }

        result.HasAlpha = hasAlpha;
        return result;
    }
}
