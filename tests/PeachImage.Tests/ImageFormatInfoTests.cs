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
    public void SupportedFormats_ContainsExactlyTheSixBuiltInFormats()
    {
        Assert.Equal(6, Image.SupportedFormats.Count);
        Assert.Equal(
            ["jpeg", "bmp", "png", "gif", "webp", "avif"],
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
