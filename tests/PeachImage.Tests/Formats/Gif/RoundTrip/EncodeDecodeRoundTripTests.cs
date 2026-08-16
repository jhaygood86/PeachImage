using PeachImage.Formats.Gif;

namespace PeachImage.Tests.Formats.Gif.RoundTrip;

public class EncodeDecodeRoundTripTests
{
    [Theory]
    [InlineData(64, 48)]
    [InlineData(5, 3)] // Non-multiple-of-8 dimensions exercise LZW sub-block/interlace-adjacent boundary math.
    [InlineData(1, 1)]
    [InlineData(255, 2)] // Exercises the 255-byte LZW sub-block boundary.
    public void LowColorCount_RoundTrips_Exactly(int width, int height)
    {
        using var source = CreateTiledImage(width, height, colorCount: 16);

        using var decoded = EncodeThenDecode(source, new GifEncoderOptions());

        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void ExactlyMaxColors_RoundTrips_Exactly()
    {
        using var source = CreateTiledImage(width: 64, height: 64, colorCount: 256);

        using var decoded = EncodeThenDecode(source, new GifEncoderOptions { MaxColors = 256 });

        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhotographicImage_RoundTrips_WithinTolerance(bool dither)
    {
        using var source = CreateGradientImage(96, 64);

        using var decoded = EncodeThenDecode(source, new GifEncoderOptions { MaxColors = 256, Dither = dither });

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);

        double averageDifference = ComputeAverageRgbDifference(source, decoded);
        Assert.True(averageDifference < 12.0, $"Average per-channel difference too high: {averageDifference:F2}");
    }

    [Fact]
    public void Rgba32Transparency_RoundTrips_Exactly()
    {
        using var source = CreateImageWithTransparentBorder(48, 32);

        using var decoded = EncodeThenDecode(source, new GifEncoderOptions());

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Cmyk32Image_Encode_Throws()
    {
        using var source = Image.Create(4, 4, PixelFormat.Cmyk32);
        using var ms = new MemoryStream();

        Assert.Throws<GifEncodingException>(() => GifEncoder.Encode(source, ms));
    }

    [Fact]
    public void AnimatedOpaqueFrames_RoundTrip_Exactly()
    {
        var frames = new List<AnimatedImageFrame>
        {
            new(CreateTiledImageRgba(32, 24, colorCount: 8, colorOffset: 0), TimeSpan.FromMilliseconds(50), FrameDisposalMethod.DoNotDispose),
            new(CreateTiledImageRgba(32, 24, colorCount: 8, colorOffset: 3), TimeSpan.FromMilliseconds(100), FrameDisposalMethod.RestoreToBackground),
            new(CreateTiledImageRgba(32, 24, colorCount: 8, colorOffset: 5), TimeSpan.FromMilliseconds(150), FrameDisposalMethod.RestoreToPrevious),
        };
        using var source = new AnimatedImage(frames, loopCount: 7);

        using var ms = new MemoryStream();
        source.Save(ms, "gif", new GifEncoderOptions());
        ms.Position = 0;

        using var decoded = AnimatedImage.Load(ms);

        Assert.Equal(3, decoded.Frames.Count);
        Assert.Equal(7, decoded.LoopCount);

        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].Disposal, decoded.Frames[i].Disposal);
            Assert.Equal(Math.Round(frames[i].Duration.TotalMilliseconds / 10.0) * 10.0, decoded.Frames[i].Duration.TotalMilliseconds);
            Assert.True(frames[i].Image.GetPixelSpan().SequenceEqual(decoded.Frames[i].Image.GetPixelSpan()));
        }
    }

    [Fact]
    public void AnimatedTransparencyWithRestoreToBackground_RoundTrips_Exactly()
    {
        using var opaqueFrame = Image.Create(20, 20, PixelFormat.Rgba32);
        FillSolid(opaqueFrame, 200, 40, 40);

        using var holeFrame = Image.Create(20, 20, PixelFormat.Rgba32);
        FillSolid(holeFrame, 40, 40, 200);
        // Punch a fully-transparent (0,0,0,0) hole in the middle — matches what a freshly-cleared
        // (RestoreToBackground) canvas looks like, so the round trip can be asserted exactly.
        for (int y = 5; y < 15; y++)
        {
            var row = holeFrame.GetRowSpan(y);
            row[(5 * 4)..(15 * 4)].Clear();
        }

        var frames = new List<AnimatedImageFrame>
        {
            new(opaqueFrame, TimeSpan.FromMilliseconds(30), FrameDisposalMethod.RestoreToBackground),
            new(holeFrame, TimeSpan.FromMilliseconds(30), FrameDisposalMethod.None),
        };
        using var source = new AnimatedImage(frames, loopCount: 0);

        using var ms = new MemoryStream();
        source.Save(ms, "gif", new GifEncoderOptions());
        ms.Position = 0;

        using var decoded = AnimatedImage.Load(ms);

        Assert.Equal(2, decoded.Frames.Count);
        Assert.True(frames[0].Image.GetPixelSpan().SequenceEqual(decoded.Frames[0].Image.GetPixelSpan()));
        Assert.True(frames[1].Image.GetPixelSpan().SequenceEqual(decoded.Frames[1].Image.GetPixelSpan()));
    }

    private static Image EncodeThenDecode(Image source, GifEncoderOptions encoderOptions)
    {
        using var ms = new MemoryStream();
        GifEncoder.Encode(source, ms, encoderOptions);

        ms.Position = 0;
        return GifDecoder.Decode(ms);
    }

    private static Image CreateTiledImage(int width, int height, int colorCount)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                int tile = ((x / 3) + (y / 3)) % colorCount;
                (byte r, byte g, byte b) = PaletteColor(tile);
                row[(x * 3) + 0] = r;
                row[(x * 3) + 1] = g;
                row[(x * 3) + 2] = b;
            }
        }

        return image;
    }

    private static Image CreateTiledImageRgba(int width, int height, int colorCount, int colorOffset)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                int tile = (((x / 3) + (y / 3) + colorOffset) % colorCount);
                (byte r, byte g, byte b) = PaletteColor(tile);
                row[(x * 4) + 0] = r;
                row[(x * 4) + 1] = g;
                row[(x * 4) + 2] = b;
                row[(x * 4) + 3] = 255;
            }
        }

        return image;
    }

    private static (byte R, byte G, byte B) PaletteColor(int index)
    {
        // A deterministic, well-separated pseudo-palette (avoids adjacent tiles quantizing to the same color).
        byte r = (byte)((index * 53) % 256);
        byte g = (byte)((index * 97) % 256);
        byte b = (byte)((index * 181) % 256);
        return (r, g, b);
    }

    private static Image CreateGradientImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[(x * 3) + 0] = (byte)((x * 255) / Math.Max(1, width - 1));
                row[(x * 3) + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                row[(x * 3) + 2] = (byte)((x + y) % 256);
            }
        }

        return image;
    }

    private static Image CreateImageWithTransparentBorder(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                bool border = x < 4 || y < 4 || x >= width - 4 || y >= height - 4;
                if (border)
                {
                    // Transparent pixels: RGB must be zero too, since a decoded transparent pixel always
                    // comes out as (0,0,0,0) — nothing is ever drawn there.
                    row.Slice(x * 4, 4).Clear();
                }
                else
                {
                    row[(x * 4) + 0] = (byte)(x % 7 * 30);
                    row[(x * 4) + 1] = (byte)(y % 5 * 40);
                    row[(x * 4) + 2] = 128;
                    row[(x * 4) + 3] = 255;
                }
            }
        }

        return image;
    }

    private static void FillSolid(Image image, byte r, byte g, byte b)
    {
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }
    }

    private static double ComputeAverageRgbDifference(Image source, Image decoded)
    {
        var a = source.GetPixelSpan();
        var b = decoded.GetPixelSpan();
        int pixelCount = source.Width * source.Height;

        double sum = 0;
        for (int i = 0; i < pixelCount; i++)
        {
            sum += Math.Abs(a[(i * 3) + 0] - b[(i * 3) + 0]);
            sum += Math.Abs(a[(i * 3) + 1] - b[(i * 3) + 1]);
            sum += Math.Abs(a[(i * 3) + 2] - b[(i * 3) + 2]);
        }

        return sum / (pixelCount * 3);
    }
}
