using PeachImage.Formats.Gif.Encoding;

namespace PeachImage.Tests.Formats.Gif.Unit.Encoding;

public class MedianCutQuantizerTests
{
    [Fact]
    public void FewerColorsThanMax_ReturnsExactPalette()
    {
        var histogram = new GifColorHistogram();
        histogram.Add(10, 20, 30);
        histogram.Add(200, 100, 50);
        histogram.Add(0, 0, 0);
        histogram.Add(255, 255, 255);

        byte[] palette = MedianCutQuantizer.BuildPalette(histogram, maxColors: 256);

        Assert.Equal(4 * 3, palette.Length);
        var colors = ToColorSet(palette);
        Assert.Contains(((byte)10, (byte)20, (byte)30), colors);
        Assert.Contains(((byte)200, (byte)100, (byte)50), colors);
        Assert.Contains(((byte)0, (byte)0, (byte)0), colors);
        Assert.Contains(((byte)255, (byte)255, (byte)255), colors);
    }

    [Fact]
    public void MoreColorsThanMax_NeverExceedsMaxColors()
    {
        var histogram = new GifColorHistogram();
        var random = new Random(42);
        for (int i = 0; i < 5000; i++)
        {
            histogram.Add((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        }

        byte[] palette = MedianCutQuantizer.BuildPalette(histogram, maxColors: 37);

        Assert.True(palette.Length / 3 <= 37);
        Assert.True(palette.Length / 3 > 0);
    }

    [Fact]
    public void EmptyHistogram_ReturnsSingleFallbackColor()
    {
        var histogram = new GifColorHistogram();

        byte[] palette = MedianCutQuantizer.BuildPalette(histogram, maxColors: 256);

        Assert.Equal(3, palette.Length);
    }

    private static HashSet<(byte, byte, byte)> ToColorSet(byte[] palette)
    {
        var set = new HashSet<(byte, byte, byte)>();
        for (int i = 0; i < palette.Length; i += 3)
        {
            set.Add((palette[i], palette[i + 1], palette[i + 2]));
        }

        return set;
    }
}
