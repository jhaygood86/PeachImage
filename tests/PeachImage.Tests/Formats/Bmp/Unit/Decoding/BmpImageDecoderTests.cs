using System.Buffers.Binary;
using PeachImage.Formats.Bmp;
using PeachImage.Formats.Bmp.Decoding;

namespace PeachImage.Tests.Formats.Bmp.Unit.Decoding;

public class BmpImageDecoderTests
{
    [Fact]
    public void Decode_Rle8_DeclaredImageSizeFarExceedsStreamLength_ThrowsInsteadOfAllocating()
    {
        // BITMAPINFOHEADER declares ~3GB of RLE-compressed data (biSizeImage), but the stream itself is only
        // a couple of bytes long past the header/palette. Previously this drove `new byte[declaredSize]`
        // directly in BmpImageDecoder.ReadRemainingData, forcing a multi-gigabyte allocation attempt from a
        // few-hundred-byte file before a single byte of the "declared" data was ever verified to exist.
        byte[] file = BuildRle8File(width: 2, height: 1, declaredImageSize: 3_000_000_000, actualDataBytes: [2, 5]);

        Assert.Throws<BmpDecodingException>(() => BmpImageDecoder.Decode(new MemoryStream(file)));
    }

    [Fact]
    public void Decode_Rle8_DeclaredImageSizeWithinStreamLength_DecodesNormally()
    {
        // 0x00,0x01 = end-of-bitmap escape: a minimal-but-valid RLE8 stream. declaredImageSize matches
        // the real data length here, so the fix's stream-length check must not reject legitimate files.
        byte[] rleData = [0, 1];
        byte[] file = BuildRle8File(width: 2, height: 1, declaredImageSize: (uint)rleData.Length, actualDataBytes: rleData);

        using var image = BmpImageDecoder.Decode(new MemoryStream(file));

        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
    }

    private static byte[] BuildRle8File(int width, int height, uint declaredImageSize, byte[] actualDataBytes)
    {
        const int fileHeaderSize = 14;
        const int dibHeaderSize = 40;
        const int paletteEntries = 2;
        const int paletteBytes = paletteEntries * 4;
        uint pixelDataOffset = fileHeaderSize + dibHeaderSize + paletteBytes;

        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        Span<byte> u16 = stackalloc byte[2];

        // BITMAPFILEHEADER
        ms.WriteByte((byte)'B');
        ms.WriteByte((byte)'M');
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); // file size (unused by decoder)
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); // reserved
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, pixelDataOffset);
        ms.Write(u32);

        // BITMAPINFOHEADER
        BinaryPrimitives.WriteUInt32LittleEndian(u32, dibHeaderSize);
        ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, width);
        ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, height); // positive = bottom-up, required for RLE.
        ms.Write(u32);
        BinaryPrimitives.WriteUInt16LittleEndian(u16, 1); // planes
        ms.Write(u16);
        BinaryPrimitives.WriteUInt16LittleEndian(u16, 8); // bitCount
        ms.Write(u16);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 1); // BI_RLE8
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, declaredImageSize);
        ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, 0); // XPelsPerMeter
        ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, 0); // YPelsPerMeter
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, paletteEntries); // ColorsUsed
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); // ColorsImportant
        ms.Write(u32);

        // Palette: paletteEntries BGRA entries.
        for (int i = 0; i < paletteEntries; i++)
        {
            ms.Write([0, 0, 0, 0]);
        }

        ms.Write(actualDataBytes);

        return ms.ToArray();
    }
}
