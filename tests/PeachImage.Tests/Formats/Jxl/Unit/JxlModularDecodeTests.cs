using System.Security.Cryptography;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlModularDecodeTests
{
    // SHA-256 of libjxl's decoded pixels (via ffmpeg's libjxl decoder, rawvideo output in the named pix_fmt) for each asset.
    [Theory]
    [InlineData("rgb_lossless.jxl", PixelFormat.Rgb24, "7cf4233ef373a8fbe4a88ad0a0dbc6c303e0a5080d36d2a74c8229e9f2899155")]
    [InlineData("gray.jxl", PixelFormat.Gray8, "25a46e60590e69c4cdae7a3a687d7a27e2e2a7ae4936fa1a1216ef7f3218cae8")]
    [InlineData("rgba.jxl", PixelFormat.Rgba32, "c71c1b5fdb21963f11790fae29ce8dbbc265b779fc822724ae559fd209f3e7f1")]
    [InlineData("rgb16.jxl", PixelFormat.Rgb48, "23abb766305897cb929be4beb8739e06c9e0f0ceaf72622053f845a8ff0c027e")]
    [InlineData("icc_lossless.jxl", PixelFormat.Rgb24, "56699dcfac1f1f988529c223f70bb5bad5c1879dc0ed4842ceecb82817cf0e02")]
    public void LosslessModular_MatchesLibjxlOutputExactly(string asset, PixelFormat format, string expectedSha256)
    {
        using var stream = new MemoryStream(JxlTestAssets.Load(asset));

        using var image = Image.Load(stream);

        Assert.Equal(format, image.PixelFormat);
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(image.GetPixelSpan())).ToLowerInvariant());
    }

    [Fact]
    public void Decode_AttachesTheEmbeddedIccProfile()
    {
        using var stream = new MemoryStream(JxlTestAssets.Load("icc_lossless.jxl"));

        using var image = Image.Load(stream);

        var profile = Assert.Single(image.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Icc);
        Assert.Equal(JxlTestAssets.Load("weird-profile.icc"), profile.Data);
    }

    [Fact]
    public void Decode_RgbaHasAlpha()
    {
        using var stream = new MemoryStream(JxlTestAssets.Load("rgba.jxl"));

        using var image = Image.Load(stream);

        Assert.True(image.HasAlpha);
    }
}
