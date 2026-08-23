using PeachImage.Formats.Tiff;
using PeachImage.Formats.Tiff.Decoding;

namespace PeachImage.Tests.Formats.Tiff.Unit.Decoding;

public class TiffHeaderReaderTests
{
    [Fact]
    public void Read_LittleEndianHeader_ParsesByteOrderAndFirstIfdOffset()
    {
        byte[] data = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0];

        var header = TiffHeaderReader.Read(data);

        Assert.Equal(TiffByteOrder.LittleEndian, header.ByteOrder);
        Assert.Equal(8u, header.FirstIfdOffset);
    }

    [Fact]
    public void Read_BigEndianHeader_ParsesByteOrderAndFirstIfdOffset()
    {
        byte[] data = [(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 16];

        var header = TiffHeaderReader.Read(data);

        Assert.Equal(TiffByteOrder.BigEndian, header.ByteOrder);
        Assert.Equal(16u, header.FirstIfdOffset);
    }

    [Fact]
    public void Read_BigTiffMagic_ThrowsUnsupportedFeature()
    {
        byte[] data = [(byte)'I', (byte)'I', 43, 0, 8, 0, 0, 0];

        Assert.Throws<TiffUnsupportedFeatureException>(() => TiffHeaderReader.Read(data));
    }

    [Fact]
    public void Read_UnknownByteOrderMark_ThrowsDecodingException()
    {
        byte[] data = [(byte)'X', (byte)'X', 42, 0, 8, 0, 0, 0];

        Assert.Throws<TiffDecodingException>(() => TiffHeaderReader.Read(data));
    }

    [Fact]
    public void Read_WrongMagicNumber_ThrowsDecodingException()
    {
        byte[] data = [(byte)'I', (byte)'I', 99, 0, 8, 0, 0, 0];

        Assert.Throws<TiffDecodingException>(() => TiffHeaderReader.Read(data));
    }

    [Fact]
    public void Read_TooShort_ThrowsDecodingException()
    {
        byte[] data = [(byte)'I', (byte)'I', 42, 0];

        Assert.Throws<TiffDecodingException>(() => TiffHeaderReader.Read(data));
    }
}
