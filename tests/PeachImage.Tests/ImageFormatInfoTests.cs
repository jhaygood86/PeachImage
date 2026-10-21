namespace PeachImage.Tests;

public class ImageFormatInfoTests
{
    [Theory]
    [InlineData("jpeg", new[] { "jpg", "jpeg", "jpe", "jfif" }, new[] { "image/jpeg" }, true, true, false, false)]
    [InlineData("bmp", new[] { "bmp" }, new[] { "image/bmp", "image/x-ms-bmp" }, true, true, true, true)]
    [InlineData("png", new[] { "png" }, new[] { "image/png" }, true, true, true, true)]
    [InlineData("gif", new[] { "gif" }, new[] { "image/gif" }, true, true, true, true)]
    [InlineData("webp", new[] { "webp" }, new[] { "image/webp" }, true, true, true, true)]
    [InlineData("avif", new[] { "avif" }, new[] { "image/avif" }, true, true, true, false)]
    [InlineData("tiff", new[] { "tiff", "tif" }, new[] { "image/tiff" }, true, false, true, false)]
    [InlineData("jxl", new[] { "jxl" }, new[] { "image/jxl" }, true, true, true, true)]
    [InlineData("ccitt", new string[0], new[] { "image/g3fax" }, true, false, false, false)]
    [InlineData("jbig2", new[] { "jb2", "jbig2" }, new[] { "image/x-jbig2" }, true, false, false, false)]
    [InlineData("jpeg2000", new[] { "jp2", "j2k", "j2c", "jpc", "jpx" }, new[] { "image/jp2", "image/j2k" }, true, false, true, false)]
    public void GetFormatInfo_ReturnsExpectedMetadata(
        string formatName,
        string[] fileExtensions,
        string[] mimeTypes,
        bool canDecode,
        bool canEncode,
        bool canDecodeTransparency,
        bool canEncodeTransparency)
    {
        var info = Image.GetFormatInfo(formatName);

        Assert.Equal(formatName, info.FormatName);
        Assert.Equal(fileExtensions, info.FileExtensions);
        Assert.Equal(mimeTypes, info.MimeTypes);
        Assert.Equal(canDecode, info.CanDecode);
        Assert.Equal(canEncode, info.CanEncode);
        Assert.Equal(canDecodeTransparency, info.CanDecodeTransparency);
        Assert.Equal(canEncodeTransparency, info.CanEncodeTransparency);
    }

    [Theory]
    [InlineData("JPEG")]
    [InlineData("Png")]
    [InlineData("AVIF")]
    public void GetFormatInfo_IsCaseInsensitive(string formatName)
    {
        var info = Image.GetFormatInfo(formatName);

        Assert.Equal(formatName.ToLowerInvariant(), info.FormatName);
    }

    [Fact]
    public void GetFormatInfo_UnknownFormatName_ThrowsUnknownImageFormatException()
    {
        var exception = Assert.Throws<UnknownImageFormatException>(() => Image.GetFormatInfo("bogus"));

        Assert.Equal("bogus", exception.FormatName);
    }

    [Fact]
    public void GetFormatInfo_NullFormatName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Image.GetFormatInfo(null!));
    }

    [Fact]
    public void SupportedFormats_ContainsExactlyTheElevenBuiltInFormats()
    {
        Assert.Equal(11, Image.SupportedFormats.Count);
        Assert.Equal(
            ["jpeg", "bmp", "png", "gif", "webp", "avif", "tiff", "jxl", "ccitt", "jbig2", "jpeg2000"],
            Image.SupportedFormats.Select(info => info.FormatName));
    }

    [Fact]
    public void SupportedFormats_MatchesGetFormatInfo_ForEveryEntry()
    {
        foreach (var info in Image.SupportedFormats)
        {
            Assert.Equal(info, Image.GetFormatInfo(info.FormatName));
        }
    }
}
