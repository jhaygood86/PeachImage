using PeachImage.Formats.Jxl;
using PeachImage.Formats.Jxl.Container;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlHeaderTests
{
    [Theory]
    [InlineData("rgb_lossless.jxl", 64, 48, PixelFormat.Rgb24, false)]
    [InlineData("rgb_lossy.jxl", 64, 48, PixelFormat.Rgb24, false)]
    [InlineData("gray.jxl", 70, 50, PixelFormat.Gray8, false)]
    [InlineData("rgba.jxl", 64, 48, PixelFormat.Rgba32, true)]
    [InlineData("rgb16.jxl", 64, 48, PixelFormat.Rgb48, false)]
    public void Identify_ReportsSizeFormatAndAlpha(string asset, int width, int height, PixelFormat format, bool alpha)
    {
        using var stream = new MemoryStream(JxlTestAssets.Load(asset));

        var info = Image.Identify(stream);

        Assert.Equal("jxl", info.FormatName);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
        Assert.Equal(format, info.PixelFormat);
        Assert.Equal(alpha, info.HasAlpha);
        Assert.False(info.IsAnimated);
    }

    [Fact]
    public void BareCodestream_ParsesHeadersWithNoMetadata()
    {
        var container = JxlContainer.Parse(JxlTestAssets.Load("rgb_lossless.jxl"));
        var headers = JxlCodestreamHeaders.Read(container.Codestream.Span);

        Assert.Equal(new JxlSize(64, 48), headers.Size);
        Assert.Null(container.Exif);
        Assert.Equal(8u, headers.Metadata.BitDepth.BitsPerSample);
        Assert.False(headers.Metadata.BitDepth.IsFloat);
        Assert.Equal(1, headers.Metadata.Orientation);
        Assert.Empty(headers.Metadata.ExtraChannels);
        Assert.Equal(JxlColorSpace.Rgb, headers.Metadata.ColorEncoding.ColorSpace);
    }

    [Fact]
    public void LossyEncode_IsXybEncoded_AndLosslessIsNot()
    {
        var lossy = JxlCodestreamHeaders.Read(JxlContainer.Parse(JxlTestAssets.Load("rgb_lossy.jxl")).Codestream.Span);
        var lossless = JxlCodestreamHeaders.Read(JxlContainer.Parse(JxlTestAssets.Load("rgb_lossless.jxl")).Codestream.Span);

        Assert.True(lossy.Metadata.XybEncoded);
        Assert.False(lossless.Metadata.XybEncoded);
    }

    [Fact]
    public void GrayImage_UsesGrayColorSpace()
    {
        var headers = JxlCodestreamHeaders.Read(JxlContainer.Parse(JxlTestAssets.Load("gray.jxl")).Codestream.Span);

        Assert.Equal(JxlColorSpace.Gray, headers.Metadata.ColorEncoding.ColorSpace);
        Assert.Equal(new JxlSize(70, 50), headers.Size);
    }

    [Fact]
    public void RgbaImage_DeclaresAnAlphaExtraChannel()
    {
        var headers = JxlCodestreamHeaders.Read(JxlContainer.Parse(JxlTestAssets.Load("rgba.jxl")).Codestream.Span);

        var extra = Assert.Single(headers.Metadata.ExtraChannels);
        Assert.Equal(JxlExtraChannelType.Alpha, extra.Type);
        Assert.Equal(0, headers.Metadata.AlphaChannelIndex);
    }

    [Fact]
    public void ContainerFile_UnwrapsCodestream()
    {
        byte[] file = JxlTestAssets.Load("rgb16.jxl");
        Assert.True(JxlContainer.HasSignature(file));

        var container = JxlContainer.Parse(file);

        Assert.Equal(0xFF, container.Codestream.Span[0]);
        Assert.Equal(0x0A, container.Codestream.Span[1]);
        Assert.Equal(16u, JxlCodestreamHeaders.Read(container.Codestream.Span).Metadata.BitDepth.BitsPerSample);
    }

    [Fact]
    public void Truncated_Codestream_ThrowsDecodingException()
    {
        byte[] bytes = JxlTestAssets.Load("rgba.jxl");

        Assert.Throws<JxlDecodingException>(() => JxlCodestreamHeaders.Read(bytes.AsSpan(0, 6)));
    }

    [Fact]
    public void NotJxl_IsRejected()
    {
        Assert.Throws<JxlDecodingException>(() => JxlContainer.Parse([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]));
        Assert.False(JxlContainer.HasSignature([0xFF, 0xD8, 0xFF]));
    }

    [Fact]
    public void Decode_VarDctFrameProducesAnImageOfTheDeclaredSize()
    {
        using var stream = new MemoryStream(JxlTestAssets.Load("rgb_lossy.jxl"));
        var info = Image.Identify(new MemoryStream(JxlTestAssets.Load("rgb_lossy.jxl")));

        using var image = Image.Load(stream);

        Assert.Equal(info.Width, image.Width);
        Assert.Equal(info.Height, image.Height);
    }

    [Fact]
    public void EmbeddedIccProfile_IsDecodedToTheOriginalBytes()
    {
        // icc_lossless.jxl was produced by libjxl from a PNG carrying weird-profile.icc, whose LUT-style
        // curves can't be expressed as an enumerated color encoding, so libjxl embeds the entropy-coded profile.
        byte[] file = JxlTestAssets.Load("icc_lossless.jxl");
        var headers = JxlCodestreamHeaders.Read(JxlContainer.Parse(file).Codestream.Span);

        Assert.True(headers.WantIcc);
        Assert.Equal(JxlTestAssets.Load("weird-profile.icc"), headers.IccProfile);
        Assert.True(headers.FrameOffset > 2);
    }

    [Fact]
    public void Identify_DoesNotNeedToDecodeTheIccProfile()
    {
        using var stream = new MemoryStream(JxlTestAssets.Load("icc_lossless.jxl"));

        var info = Image.Identify(stream);

        Assert.Equal(32, info.Width);
        Assert.Equal(PixelFormat.Rgb24, info.PixelFormat);
    }
}
