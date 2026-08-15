using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Formats.Gif.Decoding;

/// <summary>Reads the GIF signature, Logical Screen Descriptor, and Global Color Table.</summary>
internal static class GifHeaderReader
{
    public static GifHeader Read(Stream stream)
    {
        Span<byte> signature = stackalloc byte[6];
        GifStreamHelpers.ReadExactlyOrThrow(stream, signature);

        if (signature[0] != (byte)'G' || signature[1] != (byte)'I' || signature[2] != (byte)'F' || signature[3] != (byte)'8'
            || (signature[4] != (byte)'7' && signature[4] != (byte)'9') || signature[5] != (byte)'a')
        {
            throw new GifDecodingException("Not a GIF file: missing GIF87a/GIF89a signature.");
        }

        string version = System.Text.Encoding.ASCII.GetString(signature);

        Span<byte> lsd = stackalloc byte[7];
        GifStreamHelpers.ReadExactlyOrThrow(stream, lsd);

        int width = lsd[0] | (lsd[1] << 8);
        int height = lsd[2] | (lsd[3] << 8);
        byte packed = lsd[4];
        bool hasGlobalColorTable = (packed & 0x80) != 0;
        int globalColorTableSize = 2 << (packed & 0x07);
        int backgroundColorIndex = lsd[5];

        if (width <= 0 || height <= 0)
        {
            throw new GifDecodingException($"Invalid GIF canvas dimensions {width}x{height}.");
        }

        if ((long)width * height > GifDecodingLimits.MaxPixelCount)
        {
            throw new GifDecodingException($"GIF canvas {width}x{height} exceeds the maximum supported pixel count.");
        }

        byte[] globalColorTable = hasGlobalColorTable ? GifColorTable.Read(stream, globalColorTableSize) : [];

        return new GifHeader
        {
            Version = version,
            Width = width,
            Height = height,
            BackgroundColorIndex = backgroundColorIndex,
            GlobalColorTable = globalColorTable,
        };
    }
}
