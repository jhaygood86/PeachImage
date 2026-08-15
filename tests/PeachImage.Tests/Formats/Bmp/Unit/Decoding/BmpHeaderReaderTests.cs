using System.Buffers.Binary;
using PeachImage.Formats.Bmp;
using PeachImage.Formats.Bmp.Decoding;

namespace PeachImage.Tests.Formats.Bmp.Unit.Decoding;

public class BmpHeaderReaderTests
{
    [Fact]
    public void Read_CoreHeader_ParsesFieldsAndDefaultsBottomUp()
    {
        var dib = BuildCoreHeader(10, 8, 1, 24);
        var fileHeader = BuildFileHeader(0, 14 + 12);
        using var stream = BuildStream(fileHeader, dib);

        var header = BmpHeaderReader.Read(stream);

        Assert.Equal(10, header.Width);
        Assert.Equal(8, header.Height);
        Assert.False(header.TopDown);
        Assert.Equal(24, header.BitCount);
        Assert.Equal(BmpCompression.Rgb, header.Compression);
        Assert.Equal(3, header.PaletteEntrySize);
    }

    [Fact]
    public void Read_40ByteHeader_BasicRgb_ParsesFields()
    {
        var dib = BuildInfoHeader(4, 4, 1, 24, compression: 0);
        var fileHeader = BuildFileHeader(0, 14 + 40);
        using var stream = BuildStream(fileHeader, dib);

        var header = BmpHeaderReader.Read(stream);

        Assert.Equal(4, header.Width);
        Assert.Equal(4, header.Height);
        Assert.Equal(24, header.BitCount);
        Assert.Equal(4, header.PaletteEntrySize);
        Assert.Equal(54u, header.HeaderBytesConsumed);
    }

    [Fact]
    public void Read_40ByteHeader_BitfieldsQuirk_ReadsTrailingMasks()
    {
        var dib = BuildInfoHeader(4, 4, 1, 16, compression: 3); // BI_BITFIELDS
        var extraMasks = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(extraMasks.AsSpan(0), 0xF800);
        BinaryPrimitives.WriteUInt32LittleEndian(extraMasks.AsSpan(4), 0x07E0);
        BinaryPrimitives.WriteUInt32LittleEndian(extraMasks.AsSpan(8), 0x001F);
        var fileHeader = BuildFileHeader(0, 14 + 40 + 12);
        using var stream = BuildStream(fileHeader, dib, extraMasks);

        var header = BmpHeaderReader.Read(stream);

        Assert.Equal(0xF800u, header.RMask);
        Assert.Equal(0x07E0u, header.GMask);
        Assert.Equal(0x001Fu, header.BMask);
        Assert.Equal(66u, header.HeaderBytesConsumed);
    }

    [Fact]
    public void Read_V4Header_WithNonZeroAlphaMask_SetsHasAlphaMask()
    {
        var dib = BuildV4Header(4, 4, 32, compression: 3, rMask: 0x00FF0000, gMask: 0x0000FF00, bMask: 0x000000FF, aMask: 0xFF000000);
        var fileHeader = BuildFileHeader(0, 14 + 108);
        using var stream = BuildStream(fileHeader, dib);

        var header = BmpHeaderReader.Read(stream);

        Assert.True(header.HasAlphaMask);
        Assert.Equal(0xFF000000u, header.AMask);
    }

    [Fact]
    public void Read_V4Header_WithZeroAlphaMask_DoesNotSetHasAlphaMask()
    {
        var dib = BuildV4Header(4, 4, 32, compression: 0, rMask: 0x00FF0000, gMask: 0x0000FF00, bMask: 0x000000FF, aMask: 0);
        var fileHeader = BuildFileHeader(0, 14 + 108);
        using var stream = BuildStream(fileHeader, dib);

        var header = BmpHeaderReader.Read(stream);

        Assert.False(header.HasAlphaMask);
    }

    [Fact]
    public void Read_Os2ShortHeader_16Bytes_DefaultsToUncompressed()
    {
        var dib = BuildOs2Header(16, 4, 4, 1, 8, compression: null);
        var fileHeader = BuildFileHeader(0, 14 + 16);
        using var stream = BuildStream(fileHeader, dib);

        var header = BmpHeaderReader.Read(stream);

        Assert.Equal(BmpCompression.Rgb, header.Compression);
        Assert.Equal(4, header.PaletteEntrySize);
    }

    [Fact]
    public void Read_Os2Header_CompressionCode3_IsHuffmanNotBitfields_ThrowsUnsupported()
    {
        var dib = BuildOs2Header(64, 4, 4, 1, 1, compression: 3);
        var fileHeader = BuildFileHeader(0, 14 + 64);
        using var stream = BuildStream(fileHeader, dib);

        Assert.Throws<BmpDecodingException>(() => BmpHeaderReader.Read(stream));
    }

    [Fact]
    public void Read_Os2Header_CompressionCode1_IsRle8_NotThrown()
    {
        var dib = BuildOs2Header(64, 4, 4, 1, 8, compression: 1);
        var fileHeader = BuildFileHeader(0, 14 + 64);
        using var stream = BuildStream(fileHeader, dib);

        var header = BmpHeaderReader.Read(stream);

        Assert.Equal(BmpCompression.Rle8, header.Compression);
    }

    [Fact]
    public void Read_NegativeHeight_SetsTopDownAndPositiveHeight()
    {
        var dib = BuildInfoHeader(4, -4, 1, 24, compression: 0);
        var fileHeader = BuildFileHeader(0, 14 + 40);
        using var stream = BuildStream(fileHeader, dib);

        var header = BmpHeaderReader.Read(stream);

        Assert.True(header.TopDown);
        Assert.Equal(4, header.Height);
    }

    [Fact]
    public void Read_UnrecognizedDibHeaderSize_Throws()
    {
        var dib = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(0), 44);
        var fileHeader = BuildFileHeader(0, 14 + 44);
        using var stream = BuildStream(fileHeader, dib);

        Assert.Throws<BmpDecodingException>(() => BmpHeaderReader.Read(stream));
    }

    [Fact]
    public void Read_MissingSignature_Throws()
    {
        var fileHeader = new byte[14];
        fileHeader[0] = (byte)'X';
        fileHeader[1] = (byte)'X';
        var dib = BuildInfoHeader(4, 4, 1, 24, compression: 0);
        using var stream = BuildStream(fileHeader, dib);

        Assert.Throws<BmpDecodingException>(() => BmpHeaderReader.Read(stream));
    }

    [Fact]
    public void Read_InvalidPlanes_Throws()
    {
        var dib = BuildInfoHeader(4, 4, planes: 2, bitCount: 24, compression: 0);
        var fileHeader = BuildFileHeader(0, 14 + 40);
        using var stream = BuildStream(fileHeader, dib);

        Assert.Throws<BmpDecodingException>(() => BmpHeaderReader.Read(stream));
    }

    [Fact]
    public void Read_PixelDataOffsetTooSmall_Throws()
    {
        var dib = BuildInfoHeader(4, 4, 1, 24, compression: 0);
        var fileHeader = BuildFileHeader(0, pixelDataOffset: 10);
        using var stream = BuildStream(fileHeader, dib);

        Assert.Throws<BmpDecodingException>(() => BmpHeaderReader.Read(stream));
    }

    [Fact]
    public void Read_UnsupportedBitCount_Throws()
    {
        var dib = BuildInfoHeader(4, 4, 1, bitCount: 7, compression: 0);
        var fileHeader = BuildFileHeader(0, 14 + 40);
        using var stream = BuildStream(fileHeader, dib);

        Assert.Throws<BmpDecodingException>(() => BmpHeaderReader.Read(stream));
    }

    [Fact]
    public void Read_EmbeddedJpegCompression_Throws()
    {
        var dib = BuildInfoHeader(4, 4, 1, 24, compression: 4); // BI_JPEG
        var fileHeader = BuildFileHeader(0, 14 + 40);
        using var stream = BuildStream(fileHeader, dib);

        Assert.Throws<BmpDecodingException>(() => BmpHeaderReader.Read(stream));
    }

    [Fact]
    public void Read_TopDownWithRleCompression_Throws()
    {
        var dib = BuildInfoHeader(4, -4, 1, 8, compression: 1); // BI_RLE8, top-down
        var fileHeader = BuildFileHeader(0, 14 + 40 + (256 * 4));
        using var stream = BuildStream(fileHeader, dib);

        Assert.Throws<BmpDecodingException>(() => BmpHeaderReader.Read(stream));
    }

    private static MemoryStream BuildStream(byte[] fileHeader, byte[] dibHeader, byte[]? extra = null)
    {
        var ms = new MemoryStream();
        ms.Write(fileHeader);
        ms.Write(dibHeader);
        if (extra != null)
        {
            ms.Write(extra);
        }

        ms.Position = 0;
        return ms;
    }

    private static byte[] BuildFileHeader(uint fileSize, uint pixelDataOffset)
    {
        var b = new byte[14];
        b[0] = (byte)'B';
        b[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), pixelDataOffset);
        return b;
    }

    private static byte[] BuildCoreHeader(ushort width, ushort height, ushort planes, ushort bitCount)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), height);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), planes);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), bitCount);
        return b;
    }

    private static byte[] BuildInfoHeader(int width, int height, ushort planes, ushort bitCount, uint compression, uint sizeImage = 0, uint colorsUsed = 0)
    {
        var b = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), planes);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), sizeImage);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), colorsUsed);
        return b;
    }

    private static byte[] BuildV4Header(int width, int height, ushort bitCount, uint compression, uint rMask, uint gMask, uint bMask, uint aMask)
    {
        var b = new byte[108];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 108);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), rMask);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(44), gMask);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(48), bMask);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(52), aMask);
        return b;
    }

    private static byte[] BuildOs2Header(uint size, int width, int height, ushort planes, ushort bitCount, uint? compression)
    {
        var b = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), size);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), planes);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), bitCount);
        if (compression is { } c && size >= 20)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), c);
        }

        return b;
    }
}
