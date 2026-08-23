using PeachImage.Formats.Tiff;
using PeachImage.Formats.Tiff.Decoding;

namespace PeachImage.Tests.Formats.Tiff.Unit.Decoding;

public class TiffIfdReaderTests
{
    [Fact]
    public void Read_InlineSingleShortValue_ResolvesDirectly()
    {
        // Header(8) + IFD: count=1, one entry (tag=0, type=SHORT, count=1, value=42 inline), nextIfd=0.
        byte[] data =
        [
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, // header
            1, 0, // entry count
            0, 0, 3, 0, 1, 0, 0, 0, 42, 0, 0, 0, // tag=0, type=SHORT, count=1, value=42 (left-justified)
            0, 0, 0, 0, // next IFD offset
        ];
        var reader = new TiffReader(data, TiffByteOrder.LittleEndian);

        var ifd = TiffIfdReader.Read(reader, 8);

        Assert.Equal(42u, ifd.RequireUInt32(0));
    }

    [Fact]
    public void Read_OffsetIndirectedArray_ResolvesThroughOffset()
    {
        // BitsPerSample-like tag (id=1), SHORT[3] = {8,8,8} (6 bytes, doesn't fit inline), stored at offset 26.
        byte[] data =
        [
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, // header
            1, 0, // entry count
            1, 0, 3, 0, 3, 0, 0, 0, 26, 0, 0, 0, // tag=1, type=SHORT, count=3, offset=26
            0, 0, 0, 0, // next IFD offset
            8, 0, 8, 0, 8, 0, // external SHORT[3] data at offset 26
        ];
        var reader = new TiffReader(data, TiffByteOrder.LittleEndian);

        var ifd = TiffIfdReader.Read(reader, 8);

        Assert.Equal([8u, 8u, 8u], ifd.RequireUInt32Array(1));
    }

    [Fact]
    public void Read_BigEndianFile_ResolvesValuesCorrectly()
    {
        byte[] data =
        [
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, // header
            0, 1, // entry count
            0, 5, 0, 3, 0, 0, 0, 1, 0, 123, 0, 0, // tag=5, type=SHORT, count=1, value=123
            0, 0, 0, 0,
        ];
        var reader = new TiffReader(data, TiffByteOrder.BigEndian);

        var ifd = TiffIfdReader.Read(reader, 8);

        Assert.Equal(123u, ifd.RequireUInt32(5));
    }

    [Fact]
    public void HasTag_ReturnsFalseForAbsentTag()
    {
        byte[] data =
        [
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
            0, 0, // zero entries
            0, 0, 0, 0,
        ];
        var reader = new TiffReader(data, TiffByteOrder.LittleEndian);

        var ifd = TiffIfdReader.Read(reader, 8);

        Assert.False(ifd.HasTag(256));
    }

    [Fact]
    public void RequireUInt32_MissingTag_ThrowsDecodingException()
    {
        byte[] data =
        [
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
            0, 0,
            0, 0, 0, 0,
        ];
        var reader = new TiffReader(data, TiffByteOrder.LittleEndian);
        var ifd = TiffIfdReader.Read(reader, 8);

        Assert.Throws<TiffDecodingException>(() => ifd.RequireUInt32(256));
    }
}
