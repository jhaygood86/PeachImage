using PeachImage.Formats.Png.Decoding;
using PeachImage.Formats.Png.Internal;

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

        bool hasTrns = false;
        while (true)
        {
            var chunkHeader = PngChunkReader.ReadHeader(stream);
            if (chunkHeader.Type == PngChunkType.Trns)
            {
                hasTrns = true;
                break;
            }

            if (chunkHeader.Type == PngChunkType.Idat || chunkHeader.Type == PngChunkType.Iend)
            {
                break;
            }

            PngChunkReader.SkipChunk(stream, chunkHeader);
        }

        var pixelFormat = PngPixelFormatSelector.Choose(header, hasTrns);
        return new ImageInfo(header.Width, header.Height, pixelFormat, FormatName);
    }

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>.</summary>
    public static Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var pngOptions = options as PngDecoderOptions;
        var image = PngImageDecoder.Decode(stream, pngOptions);
        var result = PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
        if (!ReferenceEquals(result, image))
        {
            image.Dispose();
        }

        return result;
    }
}
