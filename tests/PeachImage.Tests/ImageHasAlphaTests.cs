using PeachImage.Formats.Avif;
using PeachImage.Formats.Tiff;
using PeachImage.Tests.Formats.Avif.Unit;
using PeachImage.Tests.Formats.Tiff.Unit;

namespace PeachImage.Tests;

public class ImageHasAlphaTests
{
    [Theory]
    [InlineData("png")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("webp")]
    [InlineData("jpeg")]
    public void OpaqueSource_RoundTrips_WithHasAlphaFalse(string formatName)
    {
        // Rgb24 (not an all-opaque Rgba32) so the source has no alpha channel to begin with -- some
        // encoders (BMP) write an alpha channel for any Rgba32 source regardless of whether every byte
        // happens to be 255, so an all-opaque Rgba32 source isn't a reliable "no alpha" fixture across
        // every format's encoder.
        using var source = CreateOpaqueRgb24Image(width: 4, height: 4);

        using var ms = new MemoryStream();
        source.Save(ms, formatName);

        ms.Position = 0;
        using var decoded = Image.Load(ms);
        Assert.False(decoded.HasAlpha);

        ms.Position = 0;
        var info = Image.Identify(ms);
        Assert.False(info.HasAlpha);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("webp")]
    public void AlphaBearingSource_RoundTrips_WithHasAlphaTrue(string formatName)
    {
        using var source = CreateImageWithOneTransparentPixel(width: 4, height: 4, includeTransparentPixel: true);

        using var ms = new MemoryStream();
        source.Save(ms, formatName);

        ms.Position = 0;
        using var decoded = Image.Load(ms);
        Assert.True(decoded.HasAlpha);

        ms.Position = 0;
        var info = Image.Identify(ms);
        Assert.True(info.HasAlpha);
    }

    [Fact]
    public void TargetPixelFormat_ForcedToRgba32_OpaqueSource_KeepsHasAlphaFalse()
    {
        using var source = CreateImageWithOneTransparentPixel(width: 4, height: 4, includeTransparentPixel: false);
        using var ms = new MemoryStream();
        source.Save(ms, "png");
        ms.Position = 0;

        using var decoded = Image.Load(ms, new DecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 });

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Assert.False(decoded.HasAlpha);
    }

    [Fact]
    public void TargetPixelFormat_ForcedToRgb24_AlphaSource_KeepsHasAlphaTrue()
    {
        using var source = CreateImageWithOneTransparentPixel(width: 4, height: 4, includeTransparentPixel: true);
        using var ms = new MemoryStream();
        source.Save(ms, "png");
        ms.Position = 0;

        using var decoded = Image.Load(ms, new DecoderOptions { TargetPixelFormat = PixelFormat.Rgb24 });

        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.True(decoded.HasAlpha);
    }

    [Fact]
    public void Tiff_Rgb24_NoExtraSamples_HasAlphaFalse()
    {
        var builder = new TiffFixtureBuilder
        {
            Width = 2,
            Height = 1,
            SamplesPerPixel = 3,
            Photometric = 2,
            Strips = [[255, 0, 0, 0, 255, 0]],
        };

        AssertTiffHasAlpha(builder.Build(), expected: false);
    }

    [Fact]
    public void Tiff_Rgba32_WithExtraSamples_HasAlphaTrue()
    {
        var builder = new TiffFixtureBuilder
        {
            Width = 2,
            Height = 1,
            SamplesPerPixel = 4,
            Photometric = 2,
            ExtraSamples = [2],
            Strips = [[255, 0, 0, 128, 0, 255, 0, 64]],
        };

        AssertTiffHasAlpha(builder.Build(), expected: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Avif_Identify_HasAlpha_MatchesAuxlAlphaItemPresence(bool includeAlpha)
    {
        byte[] file = AvifFixtureBuilder.BuildSingleItem(width: 4, height: 4, includeAlpha: includeAlpha);

        var info = AvifDecoder.Identify(new MemoryStream(file));

        Assert.Equal(includeAlpha, info.HasAlpha);
        Assert.Equal(includeAlpha, info.PixelFormat.HasAlpha());
    }

    private static void AssertTiffHasAlpha(byte[] bytes, bool expected)
    {
        using var image = TiffDecoder.Decode(new MemoryStream(bytes));
        Assert.Equal(expected, image.HasAlpha);
        Assert.Equal(expected, image.PixelFormat.HasAlpha());

        var info = TiffDecoder.Identify(new MemoryStream(bytes));
        Assert.Equal(expected, info.HasAlpha);
    }

    private static Image CreateOpaqueRgb24Image(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[(x * 3) + 0] = (byte)((x * 255) / Math.Max(1, width - 1));
                row[(x * 3) + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                row[(x * 3) + 2] = 128;
            }
        }

        return image;
    }

    /// <summary>
    /// A small opaque image, optionally with a single fully-transparent pixel — alpha values are always
    /// either 0 or 255 so the fixture is meaningful under both full-alpha formats (PNG truecolor, BMP,
    /// WebP) and formats/modes with only binary alpha (GIF, PNG indexed, both thresholded at 128).
    /// </summary>
    private static Image CreateImageWithOneTransparentPixel(int width, int height, bool includeTransparentPixel)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[(x * 4) + 0] = (byte)((x * 255) / Math.Max(1, width - 1));
                row[(x * 4) + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                row[(x * 4) + 2] = 128;
                row[(x * 4) + 3] = includeTransparentPixel && x == 0 && y == 0 ? (byte)0 : (byte)255;
            }
        }

        return image;
    }
}
