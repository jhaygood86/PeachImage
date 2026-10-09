using System.Runtime.InteropServices;

namespace PeachImage.Tests;

public class FloatPixelFormatTests
{
    [Theory]
    [InlineData(PixelFormat.GrayF32, 4, 1, false)]
    [InlineData(PixelFormat.RgbF32, 12, 3, false)]
    [InlineData(PixelFormat.RgbaF32, 16, 4, true)]
    public void Extensions_DescribeFloatFormats(PixelFormat format, int bytesPerPixel, int channels, bool alpha)
    {
        Assert.Equal(bytesPerPixel, format.GetBytesPerPixel());
        Assert.Equal(channels, format.GetChannelCount());
        Assert.Equal(4, format.GetBytesPerSample());
        Assert.Equal(alpha, format.HasAlpha());
        Assert.True(format.IsFloat());
        Assert.False(PixelFormat.Rgba64.IsFloat());
    }

    [Fact]
    public void Resize_PreservesHdrValuesAboveOne()
    {
        using var image = Image.Create(4, 4, PixelFormat.RgbF32);
        var samples = MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
        samples.Fill(4.0f);

        using var resized = image.Resize(2, 2);

        Assert.Equal(PixelFormat.RgbF32, resized.PixelFormat);
        foreach (float sample in MemoryMarshal.Cast<byte, float>(resized.GetPixelSpan()))
        {
            Assert.Equal(4.0f, sample, precision: 3);
        }
    }

    [Fact]
    public void Resize_RgbaF32_KeepsStraightAlpha()
    {
        using var image = Image.Create(2, 2, PixelFormat.RgbaF32);
        var samples = MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
        for (int i = 0; i < 4; i++)
        {
            samples[(i * 4) + 0] = 0.5f;
            samples[(i * 4) + 1] = 0.25f;
            samples[(i * 4) + 2] = 2.0f;
            samples[(i * 4) + 3] = 0.5f;
        }

        using var resized = image.Resize(4, 4);

        var result = MemoryMarshal.Cast<byte, float>(resized.GetPixelSpan());
        for (int i = 0; i < result.Length; i += 4)
        {
            Assert.Equal(0.5f, result[i], precision: 3);
            Assert.Equal(0.25f, result[i + 1], precision: 3);
            Assert.Equal(2.0f, result[i + 2], precision: 3);
            Assert.Equal(0.5f, result[i + 3], precision: 3);
        }
    }

    [Fact]
    public void Save_FloatImage_ThrowsNotSupported()
    {
        using var image = Image.Create(2, 2, PixelFormat.RgbF32);
        using var stream = new MemoryStream();

        Assert.Throws<NotSupportedException>(() => image.Save(stream, "png"));
    }
}
