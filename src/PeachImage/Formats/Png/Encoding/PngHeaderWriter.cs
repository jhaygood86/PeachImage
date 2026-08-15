using System.Buffers.Binary;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Encoding;

/// <summary>Chooses a PNG color type/bit depth for a source <see cref="PixelFormat"/> and writes IHDR.</summary>
internal static class PngHeaderWriter
{
    public static (PngColorType ColorType, byte BitDepth) ChooseColorType(PixelFormat pixelFormat) => pixelFormat switch
    {
        PixelFormat.Gray8 => (PngColorType.Grayscale, 8),
        PixelFormat.Rgb24 => (PngColorType.Truecolor, 8),
        PixelFormat.Rgba32 => (PngColorType.TruecolorAlpha, 8),
        PixelFormat.Gray16 => (PngColorType.Grayscale, 16),
        PixelFormat.Rgb48 => (PngColorType.Truecolor, 16),
        PixelFormat.Rgba64 => (PngColorType.TruecolorAlpha, 16),
        _ => throw new PngEncodingException($"PNG encoding does not support source pixel format {pixelFormat}."),
    };

    public static void WriteIhdr(Stream stream, int width, int height, PngColorType colorType, byte bitDepth, bool interlace)
    {
        Span<byte> data = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data[4..8], (uint)height);
        data[8] = bitDepth;
        data[9] = (byte)colorType;
        data[10] = 0; // compression method
        data[11] = 0; // filter method
        data[12] = interlace ? (byte)1 : (byte)0;

        PngChunkWriter.WriteChunk(stream, PngChunkType.Ihdr, data);
    }
}
