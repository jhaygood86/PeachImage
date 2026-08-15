using PeachImage.Formats.Webp;
using PeachImage.Formats.Webp.Decoding.Alpha;

namespace PeachImage.Tests.Formats.Webp.Unit.Alpha;

public class WebpAlphaHeaderTests
{
    // expectedCompression/expectedFilter are passed as int (rather than the internal enum types directly)
    // because a public [Theory] method cannot declare a parameter of a less-accessible (internal) type.
    [Theory]
    [InlineData(0b0000_0000, 0, 0)] // Uncompressed, None
    [InlineData(0b0000_0001, 1, 0)] // Lossless, None
    [InlineData(0b0000_0100, 0, 1)] // Uncompressed, Horizontal
    [InlineData(0b0000_1001, 1, 2)] // Lossless, Vertical
    [InlineData(0b0000_1101, 1, 3)] // Lossless, Gradient
    [InlineData(0b1111_0001, 1, 0)] // reserved/preprocessing bits ignored -> Lossless, None
    public void Parse_ExtractsCompressionAndFilterMethod(byte headerByte, int expectedCompression, int expectedFilter)
    {
        var header = WebpAlphaHeader.Parse(headerByte);

        Assert.Equal((WebpAlphaCompressionMethod)expectedCompression, header.CompressionMethod);
        Assert.Equal((WebpAlphaFilterMethod)expectedFilter, header.FilterMethod);
    }

    [Theory]
    [InlineData(0b0000_0010)]
    [InlineData(0b0000_0011)]
    public void Parse_InvalidCompressionMethod_Throws(byte headerByte)
    {
        Assert.Throws<WebpDecodingException>(() => WebpAlphaHeader.Parse(headerByte));
    }
}
