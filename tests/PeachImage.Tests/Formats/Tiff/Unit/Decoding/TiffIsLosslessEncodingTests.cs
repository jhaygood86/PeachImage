using PeachImage.Formats.Tiff;
using PeachImage.Tests.Formats.Tiff.Unit;

namespace PeachImage.Tests.Formats.Tiff.Unit.Decoding;

/// <summary>
/// <see cref="ImageInfo.IsLosslessEncoding"/> for TIFF: derived from the Compression tag rather than
/// hardcoded, since this decoder's entire supported compression set (1=none, 5=LZW, 32773=PackBits) is
/// lossless by construction — any TIFF that decodes successfully today is therefore always lossless, but
/// the value should still track the actual tag.
/// </summary>
public class TiffIsLosslessEncodingTests
{
    [Fact]
    public void Uncompressed_ReportsLosslessTrue()
    {
        byte[] pixels = [0, 10, 20, 30];
        var builder = new TiffFixtureBuilder
        {
            Width = 2,
            Height = 2,
            BitsPerSample = 8,
            SamplesPerPixel = 1,
            Photometric = 1,
            Compression = 1,
            Strips = [pixels],
        };

        var info = TiffDecoder.Identify(new MemoryStream(builder.Build()));

        Assert.True(info.IsLosslessEncoding);
    }

    [Fact]
    public void PackBits_ReportsLosslessTrue()
    {
        byte[] compressed = [3, (byte)'A', (byte)'B', (byte)'C', (byte)'D']; // literal run of 4
        var builder = new TiffFixtureBuilder
        {
            Width = 4,
            Height = 1,
            BitsPerSample = 8,
            SamplesPerPixel = 1,
            Photometric = 1,
            Compression = 32773,
            Strips = [compressed],
        };

        var info = TiffDecoder.Identify(new MemoryStream(builder.Build()));

        Assert.True(info.IsLosslessEncoding);
    }
}
