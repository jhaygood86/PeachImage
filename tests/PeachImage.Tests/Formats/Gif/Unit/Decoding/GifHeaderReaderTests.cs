using PeachImage.Formats.Gif;
using PeachImage.Formats.Gif.Decoding;

namespace PeachImage.Tests.Formats.Gif.Unit.Decoding;

public class GifHeaderReaderTests
{
    [Fact]
    public void ReadsSignatureDimensionsAndGlobalColorTable()
    {
        byte[] bytes = BuildHeader(width: 10, height: 20, hasGlobalColorTable: true, globalColorTableEntries: 4, backgroundColorIndex: 2);
        using var stream = new MemoryStream(bytes);

        var header = GifHeaderReader.Read(stream);

        Assert.Equal("GIF89a", header.Version);
        Assert.Equal(10, header.Width);
        Assert.Equal(20, header.Height);
        Assert.Equal(2, header.BackgroundColorIndex);
        Assert.Equal(4 * 3, header.GlobalColorTable.Length);
        Assert.Equal(255, header.GlobalColorTable[3]); // second entry's R, set below.
    }

    [Fact]
    public void NoGlobalColorTable_ReturnsEmptyArray()
    {
        byte[] bytes = BuildHeader(width: 5, height: 5, hasGlobalColorTable: false, globalColorTableEntries: 0, backgroundColorIndex: 0);
        using var stream = new MemoryStream(bytes);

        var header = GifHeaderReader.Read(stream);

        Assert.Empty(header.GlobalColorTable);
    }

    [Fact]
    public void InvalidSignature_Throws()
    {
        byte[] bytes = "NOTAGIF"u8.ToArray();
        using var stream = new MemoryStream(bytes);

        Assert.Throws<GifDecodingException>(() => GifHeaderReader.Read(stream));
    }

    [Fact]
    public void ZeroDimensions_Throws()
    {
        byte[] bytes = BuildHeader(width: 0, height: 5, hasGlobalColorTable: false, globalColorTableEntries: 0, backgroundColorIndex: 0);
        using var stream = new MemoryStream(bytes);

        Assert.Throws<GifDecodingException>(() => GifHeaderReader.Read(stream));
    }

    private static byte[] BuildHeader(int width, int height, bool hasGlobalColorTable, int globalColorTableEntries, int backgroundColorIndex)
    {
        using var stream = new MemoryStream();
        stream.Write("GIF89a"u8);
        WriteUInt16(stream, (ushort)width);
        WriteUInt16(stream, (ushort)height);

        byte packed = 0;
        if (hasGlobalColorTable)
        {
            packed |= 0x80;
            int n = 0;
            int size = 2;
            while (size < globalColorTableEntries)
            {
                size <<= 1;
                n++;
            }

            packed |= (byte)n;
        }

        stream.WriteByte(packed);
        stream.WriteByte((byte)backgroundColorIndex);
        stream.WriteByte(0);

        if (hasGlobalColorTable)
        {
            for (int i = 0; i < globalColorTableEntries; i++)
            {
                stream.WriteByte((byte)(i == 1 ? 255 : 0));
                stream.WriteByte(0);
                stream.WriteByte(0);
            }
        }

        return stream.ToArray();
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
    }
}
