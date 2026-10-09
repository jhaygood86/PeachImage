using PeachImage.Formats.Jxl.Frame;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlImageBuilderTests
{
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
