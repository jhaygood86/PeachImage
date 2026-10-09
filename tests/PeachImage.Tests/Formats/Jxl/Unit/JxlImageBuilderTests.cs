using PeachImage.Formats.Jxl.Frame;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlImageBuilderTests
{
    // A 3x2 image whose pixel (x, y) has value 10*y + x in its single (Gray8) channel.
    private static Image Source()
    {
        var image = Image.Create(3, 2, PixelFormat.Gray8);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                image.GetRowSpan(y)[x] = (byte)((10 * y) + x);
            }
        }

        return image;
    }

    private static string Dump(Image image)
    {
        var rows = new List<string>();
        for (int y = 0; y < image.Height; y++)
        {
            rows.Add(string.Join(',', image.GetRowSpan(y).ToArray().Select(b => b.ToString("00", System.Globalization.CultureInfo.InvariantCulture))));
        }

        return string.Join('|', rows);
    }

    [Theory]
    [InlineData(1, "00,01,02|10,11,12")]
    [InlineData(2, "02,01,00|12,11,10")]
    [InlineData(3, "12,11,10|02,01,00")]
    [InlineData(4, "10,11,12|00,01,02")]
    [InlineData(5, "00,10|01,11|02,12")]
    [InlineData(6, "10,00|11,01|12,02")]
    [InlineData(7, "12,02|11,01|10,00")]
    [InlineData(8, "02,12|01,11|00,10")]
    public void TestOrientationHelper_MatchesTheExifDefinition(int orientation, string expected)
    {
        using var result = JxlTestOrientation.Apply(Source(), (ImageOrientation)orientation);

        Assert.Equal(expected, Dump(result));
    }

    [Fact]
    public void TargetPixelFormat_ExpandsAndNarrows()
    {
        // rgb_lossless.jxl is 8-bit RGB: ask for 16-bit with alpha, and for float.
        byte[] file = JxlTestAssets.Load("rgb_lossless.jxl");

        using var native = Image.Load(new MemoryStream(file));
        using var rgba64 = Image.Load(new MemoryStream(file), new DecoderOptions { TargetPixelFormat = PixelFormat.Rgba64 });
        using var rgbF32 = Image.Load(new MemoryStream(file), new DecoderOptions { TargetPixelFormat = PixelFormat.RgbF32 });

        Assert.Equal(PixelFormat.Rgba64, rgba64.PixelFormat);
        Assert.Equal(PixelFormat.RgbF32, rgbF32.PixelFormat);

        var nativeBytes = native.GetPixelSpan();
        var wide = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(rgba64.GetPixelSpan());
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(rgbF32.GetPixelSpan());
        for (int i = 0; i < native.Width * native.Height; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                byte v = nativeBytes[(i * 3) + c];
                Assert.Equal(v * 257, wide[(i * 4) + c]);
                Assert.Equal(v / 255f, floats[(i * 3) + c], precision: 6);
            }

            Assert.Equal(65535, wide[(i * 4) + 3]);
        }
    }

    [Fact]
    public void TargetPixelFormat_GrayFromColor_IsRejected()
    {
        byte[] file = JxlTestAssets.Load("rgb_lossless.jxl");

        Assert.Throws<PeachImage.Formats.Jxl.JxlUnsupportedFeatureException>(() =>
            Image.Load(new MemoryStream(file), new DecoderOptions { TargetPixelFormat = PixelFormat.Gray8 }));
    }
}
