using PeachImage.Formats.Shared.Metadata;
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

        // This decoder's entire supported Compression set (1=none, 5=LZW, 32773=PackBits, enforced by
        // TiffValidation.Validate above) is lossless by construction — deriving from the tag rather than
        // hardcoding true keeps this correct if lossy (JPEG-in-TIFF) compression is ever supported later.
        bool isLosslessEncoding = descriptor.Compression is 1 or 5 or 32773;

        return new ImageInfo(descriptor.Width, descriptor.Height, descriptor.PixelFormat, FormatName, HasAlpha: descriptor.PixelFormat.HasAlpha(), IsLosslessEncoding: isLosslessEncoding, Orientation: ReadOrientation(ifd));
    }

    // Orientation (tag 274) uses the same 1-8 values as EXIF. Informational only, so a malformed entry means Normal rather than a failure.
    private static ImageOrientation ReadOrientation(TiffIfd ifd)
    {
        try
        {
            return ifd.HasTag(TiffTags.Orientation)
                ? ExifOrientationReader.FromValue((int)ifd.GetUInt32(TiffTags.Orientation, 1))
                : ImageOrientation.Normal;
        }
        catch (TiffDecodingException)
        {
            return ImageOrientation.Normal;
        }
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
