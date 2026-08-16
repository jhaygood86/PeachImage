using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif.Unit;

public class AvifDecoderTests
{
    [Fact]
    public void IsSupportedFileFormat_MajorBrandAvif_ReturnsTrue()
    {
        byte[] header = AvifFixtureBuilder.BuildSingleItem(4, 4)[..32];
        Assert.True(AvifDecoder.IsSupportedFileFormat(header));
    }

    [Fact]
    public void IsSupportedFileFormat_MajorBrandMif1WithAvifCompatible_ReturnsTrue()
    {
        byte[] header = AvifFixtureBuilder.BuildSingleItem(4, 4, majorBrand: "mif1", compatibleBrands: ["mif1", "avif", "miaf"])[..32];
        Assert.True(AvifDecoder.IsSupportedFileFormat(header));
    }

    [Fact]
    public void IsSupportedFileFormat_NonAvifData_ReturnsFalse()
    {
        byte[] header = new byte[32];
        "RIFF????WEBPVP8 "u8.ToArray().CopyTo(header, 0);
        Assert.False(AvifDecoder.IsSupportedFileFormat(header));
    }

    [Fact]
    public void IsSupportedFileFormat_TooShort_ReturnsFalse()
    {
        Assert.False(AvifDecoder.IsSupportedFileFormat(stackalloc byte[4]));
    }

    [Theory]
    [InlineData(false, false, false, true, true, PixelFormat.Rgb24)]
    [InlineData(false, false, true, true, true, PixelFormat.Rgba32)]
    [InlineData(false, true, false, true, true, PixelFormat.Gray8)]
    [InlineData(true, false, false, true, true, PixelFormat.Rgb48)]
    [InlineData(true, false, true, true, true, PixelFormat.Rgba64)]
    [InlineData(true, true, false, true, true, PixelFormat.Gray16)]
    public void Identify_ReturnsExpectedDimensionsAndPixelFormat(bool highBitdepth, bool monochrome, bool alpha, bool subX, bool subY, PixelFormat expected)
    {
        byte[] file = AvifFixtureBuilder.BuildSingleItem(37, 21, highBitdepth: highBitdepth, monochrome: monochrome, subsamplingX: subX, subsamplingY: subY, includeAlpha: alpha);

        using var stream = new MemoryStream(file);
        var info = Image.Identify(stream);

        Assert.Equal(37, info.Width);
        Assert.Equal(21, info.Height);
        Assert.Equal(expected, info.PixelFormat);
        Assert.Equal("avif", info.FormatName);
    }

    /// <summary>
    /// <see cref="AvifFixtureBuilder"/>'s AV1 tile payloads are dummy placeholder bytes, not a real AV1
    /// bitstream (it only ever needed to exercise container parsing) -- now that <c>Decode</c> actually
    /// runs the AV1 decoder against the color tile, feeding it these dummy bytes surfaces as a malformed
    /// AV1 bitstream, not the old "not implemented" placeholder.
    /// </summary>
    [Fact]
    public void Decode_DummyTilePayload_ThrowsAvifFormatException()
    {
        byte[] file = AvifFixtureBuilder.BuildSingleItem(8, 8);
        using var stream = new MemoryStream(file);

        var ex = Record.Exception(() => AvifDecoder.Decode(stream));
        Assert.IsAssignableFrom<AvifFormatException>(ex);
    }

    [Fact]
    public void Identify_TwelveBit_ThrowsUnsupportedFeature()
    {
        byte[] file = AvifFixtureBuilder.BuildSingleItem(8, 8, twelveBit: true);
        using var stream = new MemoryStream(file);

        Assert.Throws<AvifUnsupportedFeatureException>(() => AvifDecoder.Identify(stream));
    }

    [Fact]
    public void Identify_AnimatedOnlyBrand_ThrowsUnsupportedFeature()
    {
        byte[] file = AvifFixtureBuilder.BuildSingleItem(8, 8, majorBrand: "avis", compatibleBrands: ["avis", "msf1"]);
        using var stream = new MemoryStream(file);

        Assert.Throws<AvifUnsupportedFeatureException>(() => AvifDecoder.Identify(stream));
    }

    [Fact]
    public void Identify_MissingFtyp_ThrowsDecodingException()
    {
        byte[] file = AvifFixtureBuilder.BuildSingleItem(8, 8);
        int ftypBoxSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file);
        byte[] withoutFtyp = file[ftypBoxSize..];

        using var stream = new MemoryStream(withoutFtyp);
        var ex = Assert.Throws<AvifDecodingException>(() => AvifDecoder.Identify(stream));
        Assert.Contains("ftyp", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Identify_TruncatedBoxHeader_ThrowsDecodingException()
    {
        byte[] file = AvifFixtureBuilder.BuildSingleItem(8, 8);
        byte[] truncated = file[..(file.Length - 20)];

        using var stream = new MemoryStream(truncated);
        Assert.Throws<AvifDecodingException>(() => AvifDecoder.Identify(stream));
    }

    [Fact]
    public void Identify_UnrecognizedBrand_ThrowsDecodingException()
    {
        byte[] file = AvifFixtureBuilder.BuildSingleItem(8, 8, majorBrand: "jpeg", compatibleBrands: ["jpeg"]);
        using var stream = new MemoryStream(file);

        Assert.Throws<AvifDecodingException>(() => AvifDecoder.Identify(stream));
    }

    [Fact]
    public void ImageIdentify_RoutesToAvifCodec()
    {
        byte[] file = AvifFixtureBuilder.BuildSingleItem(12, 9);
        using var stream = new MemoryStream(file);

        var info = Image.Identify(stream);
        Assert.Equal("avif", info.FormatName);
    }
}
