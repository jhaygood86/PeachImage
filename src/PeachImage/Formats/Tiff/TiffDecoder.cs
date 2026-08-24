using PeachImage.Formats.Tiff.Decoding;

namespace PeachImage.Formats.Tiff;

/// <summary>Decodes baseline TIFF images: uncompressed, LZW-, and PackBits-compressed data; 1/2/4/8/16-bit depths; grayscale, RGB (with optional alpha), palette, and CMYK color. Used internally by <see cref="TiffCodec"/>.</summary>
internal static class TiffDecoder
{
    private const string FormatName = "tiff";

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] fileData = TiffStreamHelpers.BufferStream(stream);
        var header = TiffHeaderReader.Read(fileData);
        var reader = new TiffReader(fileData, header.ByteOrder);
        var ifd = TiffIfdReader.Read(reader, header.FirstIfdOffset);
        var descriptor = TiffValidation.Validate(ifd);

        return new ImageInfo(descriptor.Width, descriptor.Height, descriptor.PixelFormat, FormatName, HasAlpha: descriptor.PixelFormat.HasAlpha());
    }

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>.</summary>
    public static Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var image = TiffImageDecoder.Decode(stream);
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
