using PeachImage.Formats.Png.Decoding;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png;

/// <summary>
/// Decodes PNG images: all 5 color types at their valid bit depths (1/2/4/8/16), Adam7 interlacing,
/// palette + tRNS transparency, and the common ancillary chunks (gAMA/cHRM/sRGB/iCCP/pHYs/tEXt/zTXt/iTXt/tIME/bKGD).
/// </summary>
public sealed class PngDecoder : IImageDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <inheritdoc/>
    public string FormatName => "png";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["png"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/png"];

    /// <inheritdoc/>
    public int HeaderSize => 8;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[..8].SequenceEqual(Signature);

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream)
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

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var pngOptions = options as PngDecoderOptions;
        var image = PngImageDecoder.Decode(stream, pngOptions);
        return PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
    }
}
