using System.Buffers.Binary;
using PeachImage.Formats.Avif;
using PeachImage.Formats.Avif.Container;

namespace PeachImage.Tests.Formats.Avif.Unit;

public class AvifBoxReaderTests
{
    [Fact]
    public void ReadBoxes_StandardSize_ParsesFourCcAndPayload()
    {
        byte[] data = MakeBox("ftyp", 8);

        var boxes = AvifBoxReader.ReadBoxes(data, 0, data.Length, depth: 0);

        var box = Assert.Single(boxes);
        Assert.Equal("ftyp", box.FourCc);
        Assert.Equal(8, box.PayloadOffset);
        Assert.Equal(8, box.PayloadLength);
    }

    [Fact]
    public void ReadBoxes_SizeZero_ExtendsToContainerEnd()
    {
        var data = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0); // size == 0
        "test"u8.CopyTo(data.AsSpan(4));

        var boxes = AvifBoxReader.ReadBoxes(data, 0, data.Length, depth: 0);

        var box = Assert.Single(boxes);
        Assert.Equal(8, box.PayloadOffset);
        Assert.Equal(8, box.PayloadLength);
    }

    [Fact]
    public void ReadBoxes_LargeSize_ReadsSixtyFourBitLength()
    {
        var data = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(data, 1); // size == 1 -> largesize follows
        "test"u8.CopyTo(data.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(8), 24);

        var boxes = AvifBoxReader.ReadBoxes(data, 0, data.Length, depth: 0);

        var box = Assert.Single(boxes);
        Assert.Equal(16, box.PayloadOffset);
        Assert.Equal(8, box.PayloadLength);
    }

    [Fact]
    public void ReadBoxes_TruncatedHeader_Throws()
    {
        byte[] data = [0, 0, 0, 8, (byte)'f', (byte)'t']; // only 6 bytes, header needs 8

        Assert.Throws<AvifDecodingException>(() => AvifBoxReader.ReadBoxes(data, 0, data.Length, depth: 0));
    }

    [Fact]
    public void ReadBoxes_SizeExtendsPastContainer_Throws()
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(data, 1000); // claims to be far larger than the buffer
        "ftyp"u8.CopyTo(data.AsSpan(4));

        Assert.Throws<AvifDecodingException>(() => AvifBoxReader.ReadBoxes(data, 0, data.Length, depth: 0));
    }

    [Fact]
    public void ReadBoxes_ExceedsMaxNestingDepth_Throws()
    {
        byte[] data = MakeBox("meta", 0);

        Assert.Throws<AvifDecodingException>(() => AvifBoxReader.ReadBoxes(data, 0, data.Length, depth: 1000));
    }

    private static byte[] MakeBox(string fourCc, int payloadLength)
    {
        var data = new byte[8 + payloadLength];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(fourCc, 0, 4, data, 4);
        return data;
    }
}
