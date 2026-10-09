using PeachImage.Formats.Png.Decoding;
using PeachImage.Formats.Png.Internal;
using PeachImage.Formats.Shared.Metadata;

namespace PeachImage.Formats.Png;

/// <summary>
/// Decodes PNG images: all 5 color types at their valid bit depths (1/2/4/8/16), Adam7 interlacing,
/// palette + tRNS transparency, and the common ancillary chunks (gAMA/cHRM/sRGB/iCCP/pHYs/tEXt/zTXt/iTXt/tIME/bKGD).
/// Used internally by <see cref="PngCodec"/>.
/// </summary>
internal static class PngDecoder
{
    private const string FormatName = "png";

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        PngChunkReader.ReadSignature(stream);
        var header = PngHeaderReader.ReadIhdr(stream);

        // Scans the chunks between IHDR and the first IDAT (tRNS decides the pixel format, eXIf the orientation).
        // The eXIf chunk may legally also follow IDAT; finding it there would mean skipping the whole compressed
        // pixel stream, so Identify stays header-light and reports Normal for such files.
        bool hasTrns = false;
        var orientation = ImageOrientation.Normal;
        while (true)
        {
            var chunkHeader = PngChunkReader.ReadHeader(stream);
            if (chunkHeader.Type == PngChunkType.Idat || chunkHeader.Type == PngChunkType.Iend)
            {
                break;
            }

            if (chunkHeader.Type == PngChunkType.Trns)
            {
                hasTrns = true;
            }

            if (chunkHeader.Type == PngChunkType.Exif && chunkHeader.Length <= PngDecodingLimits.MaxAncillaryChunkBytes)
            {
                orientation = ExifOrientationReader.Read(PngChunkReader.ReadDataAndValidateCrc(stream, chunkHeader));
                continue;
            }

            PngChunkReader.SkipChunk(stream, chunkHeader);
        }

        var pixelFormat = PngPixelFormatSelector.Choose(header, hasTrns);
        return new ImageInfo(header.Width, header.Height, pixelFormat, FormatName, HasAlpha: pixelFormat.HasAlpha(), HasPreview: header.IsInterlaced, Orientation: orientation);
    }

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>.</summary>
    public static Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var pngOptions = options as PngDecoderOptions;
        var image = PngImageDecoder.Decode(stream, pngOptions, options?.PreviewAvailable, options?.TargetPixelFormat);
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
