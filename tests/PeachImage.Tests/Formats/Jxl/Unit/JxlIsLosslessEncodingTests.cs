namespace PeachImage.Tests.Formats.Jxl.Unit;

/// <summary><see cref="ImageInfo.IsLosslessEncoding"/> for JPEG XL: true for lossless Modular, false for XYB (lossy) and JPEG reconstruction.</summary>
public class JxlIsLosslessEncodingTests
{
    [Theory]
    [InlineData("rgb_lossless.jxl", true)]
    [InlineData("icc_lossless.jxl", true)]
    [InlineData("conformance_patches_lossless.jxl", true)]
    [InlineData("rgb_lossy.jxl", false)]
    [InlineData("recompressed_generated_ycc420.jxl", false)]
    [InlineData("recompressed_generated_gray.jxl", false)]
    [InlineData("recompressed_flower_small_q85_444_non_interleaved.jxl", false)]
    public void Identify_ReportsLosslessEncoding(string asset, bool expected)
    {
        using var stream = new MemoryStream(JxlTestAssets.Load(asset));

        var info = Image.Identify(stream);

        Assert.Equal(expected, info.IsLosslessEncoding);
    }
}
