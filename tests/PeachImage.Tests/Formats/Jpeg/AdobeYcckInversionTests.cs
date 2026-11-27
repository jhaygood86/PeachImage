using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jpeg.ColorConversion;
using PeachImage.Tests.PixelFormatConversion;

namespace PeachImage.Tests.Formats.Jpeg;

/// <summary>
/// Adobe YCCK (APP14 transform 2) stores inverted ink in all four channels, like transform 0. Every route to RGB must see
/// standard CMYK (0 = no ink): the fixtures are tiny hand-assembled JPEGs with a hand-built CMYK ICC profile, so a
/// pure cyan file must come out cyan (not near black) through each of them.
/// </summary>
public class AdobeYcckInversionTests
{
    // Pure cyan ink as an Adobe YCCK file stores it: inverted samples (0, 255, 255, 255) with the first three through YCbCr.
    private static byte[] CyanYcck(byte[]? profile) => ProfilelessCmykTests.BuildFourComponentJpeg(YcckSamples(255, 0, 0, 0), adobeTransform: 2, profile);

    private static byte[] YcckSamples(byte c, byte m, byte y, byte k)
    {
        (byte yy, byte cb, byte cr) = ScalarColorConverter.ConvertRgbPixel(c, m, y);
        return [yy, cb, cr, (byte)(255 - k)];
    }

    private static void AssertCyan(Image image, string route)
    {
        using (image)
        {
            Assert.Equal(PixelFormat.Rgba32, image.PixelFormat);
            var p = image.GetPixelSpan();
            Assert.True(p[0] < 70 && p[1] > 120 && p[2] > 160 && p[3] == 255, $"{route}: {p[0]},{p[1]},{p[2]},{p[3]}");
        }
    }

    [Fact]
    public void Identify_ReportsYcckAsAdobeInverted()
    {
        var info = Image.Identify(new MemoryStream(CyanYcck(null)));
        Assert.True(info.IsYcck);
        Assert.True(info.IsAdobeInvertedCmyk);
        Assert.Equal(PixelFormat.Cmyk32, info.PixelFormat);
    }

    [Fact]
    public void Load_ReturnsStandardCmyk()
    {
        using var cmyk = Image.Load(CyanYcck(null));
        var p = cmyk.GetPixelSpan();
        Assert.InRange(p[0], 253, 255);
        Assert.InRange(p[1], 0, 2);
        Assert.InRange(p[2], 0, 2);
        Assert.InRange(p[3], 0, 2);
    }

    [Fact]
    public void KeepAdobeCmykInverted_ReturnsTheComplementOfTheDefault()
    {
        byte[] jpeg = CyanYcck(null);
        using var standard = Image.Load(jpeg);
        using var kept = Image.Load(jpeg, new JpegDecoderOptions { KeepAdobeCmykInverted = true });
        var a = standard.GetPixelSpan();
        var b = kept.GetPixelSpan();
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(255 - a[i], b[i]);
        }
    }

    [Fact]
    public void EveryRouteToRgb_AgreesOnCyan_WithAnEmbeddedProfile()
    {
        byte[] profile = TinyCmykProfile.Build();
        byte[] jpeg = CyanYcck(profile);

        using (var standard = Image.Load(jpeg))
        {
            AssertCyan(standard.ConvertToSrgb(), "ConvertToSrgb");
        }

        AssertCyan(Image.Load(jpeg, new DecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 }), "TargetPixelFormat Rgba32");

        using (var raw = Image.Load(jpeg, new JpegDecoderOptions { DecodeRawYcck = true }))
        {
            Assert.Equal(PixelFormat.Ycck32, raw.PixelFormat);
            AssertCyan(raw.ConvertToSrgb(), "DecodeRawYcck + ConvertToSrgb");
        }

        AssertCyan(Image.LoadBitmap(jpeg, new BitmapRequest { Orientation = BitmapOrientation.None, OutputFormat = PixelFormat.Rgba32, PremultiplyAlpha = BitmapPremultiply.None }), "LoadBitmap");

        // The public IccColorProfile route over the standard samples agrees.
        using var cmyk = Image.Load(jpeg);
        var icc = cmyk.Metadata.GetIccColorProfile()!;
        var rgba = new byte[4];
        icc.ConvertToSrgb(cmyk.GetPixelSpan()[..4], rgba, 1);
        Assert.True(rgba[0] < 70 && rgba[1] > 120 && rgba[2] > 160, $"IccColorProfile: {rgba[0]},{rgba[1]},{rgba[2]}");
    }

    [Fact]
    public void ProfilelessRoutes_AgreeOnCyan()
    {
        byte[] jpeg = CyanYcck(null);
        using var cmyk = Image.Load(jpeg);
        using var rgb = cmyk.ConvertTo(PixelFormat.Rgba32);
        Assert.InRange(rgb.GetPixelSpan()[0], 0, 2);
        Assert.InRange(rgb.GetPixelSpan()[1], 253, 255);
        Assert.InRange(rgb.GetPixelSpan()[2], 253, 255);

        using var viaBitmap = Image.LoadBitmap(jpeg, new BitmapRequest { Orientation = BitmapOrientation.None, OutputFormat = PixelFormat.Rgba32, PremultiplyAlpha = BitmapPremultiply.None });
        Assert.InRange(viaBitmap.GetPixelSpan()[0], 0, 2);
        Assert.InRange(viaBitmap.GetPixelSpan()[1], 253, 255);
    }

    [Fact]
    public void AdobeTransformZeroCmyk_IsStillInvertedExactlyOnce()
    {
        // Direct Adobe CMYK for pure cyan stores (0, 255, 255, 255).
        byte[] jpeg = ProfilelessCmykTests.BuildFourComponentJpeg([0, 255, 255, 255], adobeTransform: 0);
        using var cmyk = Image.Load(jpeg);
        var p = cmyk.GetPixelSpan();
        Assert.Equal(new byte[] { 255, 0, 0, 0 }, new[] { p[0], p[1], p[2], p[3] });
    }
}
