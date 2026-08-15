using System.Buffers.Binary;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Decoding;

/// <summary>Reads and validates the mandatory-first IHDR chunk.</summary>
internal static class PngHeaderReader
{
    private static readonly byte[] ValidBitDepths1248 = [1, 2, 4, 8];
    private static readonly byte[] ValidBitDepths12481 = [1, 2, 4, 8, 16];
    private static readonly byte[] ValidBitDepths816 = [8, 16];

    public static PngHeader ReadIhdr(Stream stream)
    {
        var chunkHeader = PngChunkReader.ReadHeader(stream);
        if (chunkHeader.Type != PngChunkType.Ihdr)
        {
            throw new PngDecodingException($"Expected IHDR as the first chunk, found '{chunkHeader.Type}'.");
        }

        if (chunkHeader.Length != 13)
        {
            throw new PngDecodingException($"IHDR chunk has an invalid length of {chunkHeader.Length} bytes (expected 13).");
        }

        byte[] data = PngChunkReader.ReadDataAndValidateCrc(stream, chunkHeader);

        int width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)));
        int height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)));
        byte bitDepth = data[8];
        var colorType = (PngColorType)data[9];
        byte compressionMethod = data[10];
        byte filterMethod = data[11];
        byte interlaceMethod = data[12];

        if (width <= 0 || height <= 0)
        {
            throw new PngDecodingException($"IHDR declares invalid dimensions {width}x{height}.");
        }

        if ((long)width * height > PngDecodingLimits.MaxPixelCount)
        {
            throw new PngDecodingException($"IHDR declares a pixel count ({(long)width * height}) exceeding the {PngDecodingLimits.MaxPixelCount} limit.");
        }

        byte[] allowedDepths = colorType switch
        {
            PngColorType.Grayscale => ValidBitDepths12481,
            PngColorType.Truecolor => ValidBitDepths816,
            PngColorType.Palette => ValidBitDepths1248,
            PngColorType.GrayscaleAlpha => ValidBitDepths816,
            PngColorType.TruecolorAlpha => ValidBitDepths816,
            _ => throw new PngDecodingException($"Unsupported PNG color type {(byte)colorType}."),
        };

        if (Array.IndexOf(allowedDepths, bitDepth) < 0)
        {
            throw new PngDecodingException($"Bit depth {bitDepth} is not valid for color type {(byte)colorType}.");
        }

        if (compressionMethod != 0)
        {
            throw new PngDecodingException($"Unsupported PNG compression method {compressionMethod}.");
        }

        if (filterMethod != 0)
        {
            throw new PngDecodingException($"Unsupported PNG filter method {filterMethod}.");
        }

        if (interlaceMethod is not (0 or 1))
        {
            throw new PngDecodingException($"Unsupported PNG interlace method {interlaceMethod}.");
        }

        return new PngHeader(width, height, bitDepth, colorType, compressionMethod, filterMethod, interlaceMethod);
    }
}
