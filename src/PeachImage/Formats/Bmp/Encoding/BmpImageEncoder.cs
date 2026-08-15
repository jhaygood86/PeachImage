using System.Buffers.Binary;
using PeachImage.Formats.Bmp.Decoding;

namespace PeachImage.Formats.Bmp.Encoding;

/// <summary>Orchestrates a full BMP encode: writes the file header, DIB header, optional palette, and pixel data for each supported <see cref="PixelFormat"/>.</summary>
internal static class BmpImageEncoder
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int V4HeaderSize = 108;
    private const int GrayscalePaletteBytes = 256 * 4;

    public static void Encode(Image image, Stream stream, BmpEncoderOptions options)
    {
        switch (image.PixelFormat)
        {
            case PixelFormat.Rgb24:
                EncodeRgb24(image, stream);
                break;
            case PixelFormat.Gray8:
                EncodeGray8(image, stream, options);
                break;
            case PixelFormat.Rgba32:
                EncodeRgba32(image, stream);
                break;
            case PixelFormat.Cmyk32:
                throw new BmpEncodingException("CMYK is not yet supported.");
            default:
                throw new BmpEncodingException($"Cannot encode pixel format {image.PixelFormat} as BMP.");
        }
    }

    private static void EncodeRgb24(Image image, Stream stream)
    {
        int width = image.Width, height = image.Height;
        int paddedRowBytes = BmpRowPacker.GetPaddedRowByteCount(width, 24);
        uint pixelDataOffset = FileHeaderSize + InfoHeaderSize;
        uint pixelDataSize = (uint)(paddedRowBytes * height);

        WriteFileHeader(stream, pixelDataOffset + pixelDataSize, pixelDataOffset);
        WriteInfoHeader(stream, width, height, 24, BmpCompression.Rgb, pixelDataSize, colorsUsed: 0);

        Span<byte> rowBuffer = paddedRowBytes <= 4096 ? stackalloc byte[paddedRowBytes] : new byte[paddedRowBytes];
        for (int y = height - 1; y >= 0; y--)
        {
            BmpRowPacker.PackRgb24Row(image.GetRowSpan(y), rowBuffer);
            stream.Write(rowBuffer);
        }
    }

    private static void EncodeRgba32(Image image, Stream stream)
    {
        int width = image.Width, height = image.Height;
        int rowBytes = width * 4; // Never padded — already a multiple of 4.
        uint pixelDataOffset = FileHeaderSize + V4HeaderSize;
        uint pixelDataSize = (uint)(rowBytes * height);

        WriteFileHeader(stream, pixelDataOffset + pixelDataSize, pixelDataOffset);
        WriteV4Header(stream, width, height, pixelDataSize);

        Span<byte> rowBuffer = rowBytes <= 4096 ? stackalloc byte[rowBytes] : new byte[rowBytes];
        for (int y = height - 1; y >= 0; y--)
        {
            BmpRowPacker.PackRgba32Row(image.GetRowSpan(y), rowBuffer);
            stream.Write(rowBuffer);
        }
    }

    private static void EncodeGray8(Image image, Stream stream, BmpEncoderOptions options)
    {
        int width = image.Width, height = image.Height;

        if (options.UseRunLengthEncoding)
        {
            // Build a bottom-up, unpadded index buffer (one byte per pixel) to feed the RLE encoder — image
            // row 0 (top) maps to the last buffer row, image row (height-1) (bottom) maps to buffer row 0.
            var indices = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                int destRow = height - 1 - y;
                image.GetRowSpan(y).CopyTo(indices.AsSpan(destRow * width, width));
            }

            byte[] compressed = BmpRleEncoder.EncodeRle8(indices, width, height);
            uint pixelDataOffset = FileHeaderSize + InfoHeaderSize + GrayscalePaletteBytes;

            WriteFileHeader(stream, pixelDataOffset + (uint)compressed.Length, pixelDataOffset);
            WriteInfoHeader(stream, width, height, 8, BmpCompression.Rle8, (uint)compressed.Length, colorsUsed: 256);
            BmpPaletteWriter.WriteGrayscalePalette(stream);
            stream.Write(compressed);
        }
        else
        {
            int paddedRowBytes = BmpRowPacker.GetPaddedRowByteCount(width, 8);
            uint pixelDataOffset = FileHeaderSize + InfoHeaderSize + GrayscalePaletteBytes;
            uint pixelDataSize = (uint)(paddedRowBytes * height);

            WriteFileHeader(stream, pixelDataOffset + pixelDataSize, pixelDataOffset);
            WriteInfoHeader(stream, width, height, 8, BmpCompression.Rgb, pixelDataSize, colorsUsed: 256);
            BmpPaletteWriter.WriteGrayscalePalette(stream);

            Span<byte> rowBuffer = paddedRowBytes <= 4096 ? stackalloc byte[paddedRowBytes] : new byte[paddedRowBytes];
            for (int y = height - 1; y >= 0; y--)
            {
                BmpRowPacker.PackGray8IndexedRow(image.GetRowSpan(y), rowBuffer);
                stream.Write(rowBuffer);
            }
        }
    }

    private static void WriteFileHeader(Stream stream, uint fileSize, uint pixelDataOffset)
    {
        Span<byte> header = stackalloc byte[FileHeaderSize];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..6], fileSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..10], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[10..14], pixelDataOffset);
        stream.Write(header);
    }

    private static void WriteInfoHeader(Stream stream, int width, int height, int bitCount, BmpCompression compression, uint sizeImage, uint colorsUsed)
    {
        Span<byte> header = stackalloc byte[InfoHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], height); // Positive: bottom-up (always emitted).
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..14], 1); // Planes.
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..16], (ushort)bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], MapCompressionCode(compression));
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], sizeImage);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], 0); // XPelsPerMeter.
        BinaryPrimitives.WriteInt32LittleEndian(header[28..32], 0); // YPelsPerMeter.
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..36], colorsUsed);
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..40], 0); // ColorsImportant.
        stream.Write(header);
    }

    private static void WriteV4Header(Stream stream, int width, int height, uint sizeImage)
    {
        Span<byte> header = stackalloc byte[V4HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], V4HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], height); // Positive: bottom-up (always emitted).
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..14], 1); // Planes.
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..16], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], MapCompressionCode(BmpCompression.Bitfields));
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], sizeImage);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], 0); // XPelsPerMeter.
        BinaryPrimitives.WriteInt32LittleEndian(header[28..32], 0); // YPelsPerMeter.
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..36], 0); // ColorsUsed.
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..40], 0); // ColorsImportant.
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..44], 0x00FF0000); // Red mask.
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..48], 0x0000FF00); // Green mask.
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..52], 0x000000FF); // Blue mask.
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..56], 0xFF000000); // Alpha mask.
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..60], 0x57696E20); // bV4CSType = LCS_WINDOWS_COLOR_SPACE ("Win ").
        header[60..108].Clear(); // CIEXYZTRIPLE endpoints + gamma — ignored whenever CSType != LCS_CALIBRATED_RGB.
        stream.Write(header);
    }

    private static uint MapCompressionCode(BmpCompression compression) => compression switch
    {
        BmpCompression.Rgb => 0,
        BmpCompression.Rle8 => 1,
        BmpCompression.Rle4 => 2,
        BmpCompression.Bitfields => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "Unsupported BMP encode compression."),
    };
}
