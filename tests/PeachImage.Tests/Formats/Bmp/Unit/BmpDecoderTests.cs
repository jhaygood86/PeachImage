using System.Buffers.Binary;
using PeachImage.Formats.Bmp;

namespace PeachImage.Tests.Formats.Bmp.Unit;

public class BmpDecoderTests
{
    [Fact]
    public void Identify_IntMinValueHeight_ThrowsInsteadOfReturningCorruptInfo()
    {
        // Negating Int32.MinValue overflows back to Int32.MinValue (still negative) in unchecked arithmetic,
        // which used to silently bypass BmpHeaderReader's width*height-vs-MaxPixelCount guard (a positive
        // width times a negative height is never greater than a positive limit) and let Identify() return an
        // ImageInfo with a garbage negative Height and no exception at all.
        byte[] file = BuildMinimalFile(width: 4, height: int.MinValue);

        Assert.Throws<BmpDecodingException>(() => BmpDecoder.Identify(new MemoryStream(file)));
    }

    [Fact]
    public void Decode_IntMinValueHeight_ThrowsBmpDecodingException_NotArgumentOutOfRangeException()
    {
        // Same root cause as above, but on the Decode() path: the corrupt negative height reaches
        // Image.Create, which throws a raw ArgumentOutOfRangeException — escaping Image.TryLoad's
        // documented "only ImageFormatException" contract.
        byte[] file = BuildMinimalFile(width: 4, height: int.MinValue);

        Assert.Throws<BmpDecodingException>(() => BmpDecoder.Decode(new MemoryStream(file)));
    }

    private static byte[] BuildMinimalFile(int width, int height)
    {
        const int fileHeaderSize = 14;
        const int dibHeaderSize = 40;
        uint pixelDataOffset = fileHeaderSize + dibHeaderSize;

        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        Span<byte> u16 = stackalloc byte[2];

        ms.WriteByte((byte)'B');
        ms.WriteByte((byte)'M');
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); // file size (unused by decoder)
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); // reserved
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, pixelDataOffset);
        ms.Write(u32);

        BinaryPrimitives.WriteUInt32LittleEndian(u32, dibHeaderSize);
        ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, width);
        ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, height);
        ms.Write(u32);
        BinaryPrimitives.WriteUInt16LittleEndian(u16, 1); // planes
        ms.Write(u16);
        BinaryPrimitives.WriteUInt16LittleEndian(u16, 24); // bitCount
        ms.Write(u16);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); // BI_RGB
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); // sizeImage
        ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, 0); // XPelsPerMeter
        ms.Write(u32);
        BinaryPrimitives.WriteInt32LittleEndian(u32, 0); // YPelsPerMeter
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); // ColorsUsed
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); // ColorsImportant
        ms.Write(u32);

        return ms.ToArray();
    }
}
