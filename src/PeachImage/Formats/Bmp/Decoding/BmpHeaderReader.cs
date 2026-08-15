using System.Buffers.Binary;
using PeachImage.Formats.Bmp.Internal;

namespace PeachImage.Formats.Bmp.Decoding;

/// <summary>
/// Reads a BMP file header (BITMAPFILEHEADER) plus whichever DIB header variant follows it, dispatching by
/// the DIB header's own leading size field rather than modeling one type per variant — every recognized
/// header shares identical offsets for the fields actually needed.
/// </summary>
internal static class BmpHeaderReader
{
    private const int FileHeaderSize = 14;

    public static BmpHeader Read(Stream stream)
    {
        Span<byte> fileHeader = stackalloc byte[FileHeaderSize];
        BmpStreamHelpers.ReadExactlyOrThrow(stream, fileHeader);

        if (fileHeader[0] != (byte)'B' || fileHeader[1] != (byte)'M')
        {
            throw new BmpDecodingException("Not a BMP file: missing 'BM' signature.");
        }

        uint pixelDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(fileHeader[10..]);

        Span<byte> sizeField = stackalloc byte[4];
        BmpStreamHelpers.ReadExactlyOrThrow(stream, sizeField);
        uint dibHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(sizeField);

        var header = dibHeaderSize switch
        {
            12 => ReadCoreHeader(stream, dibHeaderSize),
            40 or 52 or 56 or 108 or 124 => ReadWindowsHeader(stream, dibHeaderSize),
            16 or 20 or 24 or 28 or 32 or 36 or 64 => ReadOs2Header(stream, dibHeaderSize),
            _ => throw new BmpDecodingException($"Unrecognized or unsupported DIB header size: {dibHeaderSize}."),
        };

        if (header.Width <= 0)
        {
            throw new BmpDecodingException($"Invalid BMP width: {header.Width}.");
        }

        if (header.BitCount is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            throw new BmpDecodingException($"Unsupported BMP bit depth: {header.BitCount}.");
        }

        if ((long)header.Width * header.Height > BmpDecodingLimits.MaxPixelCount)
        {
            throw new BmpDecodingException($"BMP dimensions {header.Width}x{header.Height} exceed the maximum supported pixel count.");
        }

        if (header.Compression is BmpCompression.Jpeg or BmpCompression.Png or BmpCompression.Unsupported)
        {
            throw new BmpDecodingException($"Unsupported BMP compression: {header.Compression}.");
        }

        if (header.TopDown && header.Compression is BmpCompression.Rle8 or BmpCompression.Rle4)
        {
            throw new BmpDecodingException("Top-down BMP images cannot use RLE compression.");
        }

        if (pixelDataOffset < FileHeaderSize + dibHeaderSize)
        {
            throw new BmpDecodingException("Invalid BMP pixel data offset.");
        }

        return header with { PixelDataOffset = pixelDataOffset };
    }

    private static BmpHeader ReadCoreHeader(Stream stream, uint dibHeaderSize)
    {
        Span<byte> rest = stackalloc byte[8];
        BmpStreamHelpers.ReadExactlyOrThrow(stream, rest);

        int width = BinaryPrimitives.ReadUInt16LittleEndian(rest[..2]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(rest[2..4]);
        int planes = BinaryPrimitives.ReadUInt16LittleEndian(rest[4..6]);
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(rest[6..8]);

        ValidatePlanes(planes);

        if (height == 0)
        {
            throw new BmpDecodingException("BMP height cannot be zero.");
        }

        return new BmpHeader
        {
            HeaderBytesConsumed = FileHeaderSize + dibHeaderSize,
            DibHeaderSize = dibHeaderSize,
            Width = width,
            Height = height,
            TopDown = false,
            BitCount = bitCount,
            Compression = BmpCompression.Rgb,
            PaletteEntrySize = 3,
        };
    }

    private static BmpHeader ReadWindowsHeader(Stream stream, uint dibHeaderSize)
    {
        int restLength = (int)dibHeaderSize - 4;
        Span<byte> rest = stackalloc byte[restLength];
        BmpStreamHelpers.ReadExactlyOrThrow(stream, rest);

        int width = BinaryPrimitives.ReadInt32LittleEndian(rest[..4]);
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(rest[4..8]);
        int planes = BinaryPrimitives.ReadUInt16LittleEndian(rest[8..10]);
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(rest[10..12]);
        uint compressionCode = BinaryPrimitives.ReadUInt32LittleEndian(rest[12..16]);
        uint declaredImageSize = rest.Length >= 20 ? BinaryPrimitives.ReadUInt32LittleEndian(rest[16..20]) : 0;
        uint colorsUsed = rest.Length >= 32 ? BinaryPrimitives.ReadUInt32LittleEndian(rest[28..32]) : 0;

        var compression = MapWindowsCompression(compressionCode);

        uint rMask = 0, gMask = 0, bMask = 0, aMask = 0;
        bool hasAlphaMask = false;
        uint extraMaskBytes = 0;

        if (rest.Length >= 48)
        {
            rMask = BinaryPrimitives.ReadUInt32LittleEndian(rest[36..40]);
            gMask = BinaryPrimitives.ReadUInt32LittleEndian(rest[40..44]);
            bMask = BinaryPrimitives.ReadUInt32LittleEndian(rest[44..48]);

            if (rest.Length >= 52)
            {
                aMask = BinaryPrimitives.ReadUInt32LittleEndian(rest[48..52]);
                hasAlphaMask = aMask != 0;
            }
        }
        else if (compression is BmpCompression.Bitfields or BmpCompression.AlphaBitfields)
        {
            // Classic Windows 3.x quirk: a 40-byte BITMAPINFOHEADER declaring BI_BITFIELDS carries its R/G/B
            // (and, for BI_ALPHABITFIELDS, A) masks in extra DWORDs immediately following the declared
            // header bytes, before the palette/pixel data — not inside the header itself.
            int maskCount = compression == BmpCompression.AlphaBitfields ? 4 : 3;
            Span<byte> masks = stackalloc byte[maskCount * 4];
            BmpStreamHelpers.ReadExactlyOrThrow(stream, masks);
            rMask = BinaryPrimitives.ReadUInt32LittleEndian(masks[0..4]);
            gMask = BinaryPrimitives.ReadUInt32LittleEndian(masks[4..8]);
            bMask = BinaryPrimitives.ReadUInt32LittleEndian(masks[8..12]);
            extraMaskBytes = (uint)masks.Length;

            if (maskCount == 4)
            {
                aMask = BinaryPrimitives.ReadUInt32LittleEndian(masks[12..16]);
                hasAlphaMask = aMask != 0;
            }
        }

        ValidatePlanes(planes);

        bool topDown = rawHeight < 0;
        int height = topDown ? -rawHeight : rawHeight;
        if (height == 0)
        {
            throw new BmpDecodingException("BMP height cannot be zero.");
        }

        return new BmpHeader
        {
            HeaderBytesConsumed = FileHeaderSize + dibHeaderSize + extraMaskBytes,
            DibHeaderSize = dibHeaderSize,
            Width = width,
            Height = height,
            TopDown = topDown,
            BitCount = bitCount,
            Compression = compression,
            ColorsUsed = colorsUsed,
            DeclaredImageSize = declaredImageSize,
            RMask = rMask,
            GMask = gMask,
            BMask = bMask,
            AMask = aMask,
            HasAlphaMask = hasAlphaMask,
            PaletteEntrySize = 4,
        };
    }

    private static BmpHeader ReadOs2Header(Stream stream, uint dibHeaderSize)
    {
        int restLength = (int)dibHeaderSize - 4;
        Span<byte> rest = stackalloc byte[restLength];
        BmpStreamHelpers.ReadExactlyOrThrow(stream, rest);

        int width = BinaryPrimitives.ReadInt32LittleEndian(rest[..4]);
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(rest[4..8]);
        int planes = BinaryPrimitives.ReadUInt16LittleEndian(rest[8..10]);
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(rest[10..12]);

        var compression = BmpCompression.Rgb;
        if (rest.Length >= 16)
        {
            uint compressionCode = BinaryPrimitives.ReadUInt32LittleEndian(rest[12..16]);
            compression = MapOs2Compression(compressionCode);
        }

        uint declaredImageSize = rest.Length >= 20 ? BinaryPrimitives.ReadUInt32LittleEndian(rest[16..20]) : 0;

        ValidatePlanes(planes);

        bool topDown = rawHeight < 0;
        int height = topDown ? -rawHeight : rawHeight;
        if (height == 0)
        {
            throw new BmpDecodingException("BMP height cannot be zero.");
        }

        return new BmpHeader
        {
            HeaderBytesConsumed = FileHeaderSize + dibHeaderSize,
            DibHeaderSize = dibHeaderSize,
            Width = width,
            Height = height,
            TopDown = topDown,
            BitCount = bitCount,
            Compression = compression,
            DeclaredImageSize = declaredImageSize,
            PaletteEntrySize = 4,
        };
    }

    private static void ValidatePlanes(int planes)
    {
        if (planes != 1)
        {
            throw new BmpDecodingException($"Unsupported BMP planes count: {planes}. Only 1 is supported.");
        }
    }

    private static BmpCompression MapWindowsCompression(uint code) => code switch
    {
        0 => BmpCompression.Rgb,
        1 => BmpCompression.Rle8,
        2 => BmpCompression.Rle4,
        3 => BmpCompression.Bitfields,
        4 => BmpCompression.Jpeg,
        5 => BmpCompression.Png,
        6 => BmpCompression.AlphaBitfields,
        _ => BmpCompression.Unsupported,
    };

    private static BmpCompression MapOs2Compression(uint code) => code switch
    {
        0 => BmpCompression.Rgb,
        1 => BmpCompression.Rle8,
        2 => BmpCompression.Rle4,
        _ => BmpCompression.Unsupported, // 3 = Huffman 1D, 4 = RLE24 — not implemented.
    };
}
