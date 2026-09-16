using PeachImage.Formats.Webp;

namespace PeachImage.Tests.Formats.Webp.Unit;

/// <summary><see cref="ImageInfo.IsLosslessEncoding"/> for WebP: reflects whether the container's bitstream chunk is VP8L (lossless) or VP8 (lossy).</summary>
public class WebpIsLosslessEncodingTests
{
    [Fact]
    public void LosslessEncode_ReportsLosslessTrue()
    {
        var source = CreateSolidColorImage(8, 8);
        var info = EncodeThenIdentify(source, new WebpEncoderOptions { Lossless = true });

        Assert.True(info.IsLosslessEncoding);
    }

    [Fact]
    public void LossyEncode_ReportsLosslessFalse()
    {
        var source = CreateSolidColorImage(8, 8);
        var info = EncodeThenIdentify(source, new WebpEncoderOptions { Lossless = false });

        Assert.False(info.IsLosslessEncoding);
    }

    [Fact]
    public void AnimatedWebp_ReportsLosslessFalseRegardlessOfFrameEncoding()
    {
        var frames = new List<AnimatedImageFrame>
        {
            new(CreateSolidColorImage(4, 4), TimeSpan.FromMilliseconds(20), FrameDisposalMethod.None),
            new(CreateSolidColorImage(4, 4), TimeSpan.FromMilliseconds(20), FrameDisposalMethod.None),
        };
        var animation = new AnimatedImage(frames, width: 4, height: 4, loopCount: 0);
        using var ms = new MemoryStream();
        animation.Save(ms, "webp", new WebpEncoderOptions { Lossless = true });
        ms.Position = 0;

        var info = Image.Identify(ms);

        // Lossy/lossless is a genuinely per-ANMF-frame property; Identify's cheap VP8X-only fast path for
        // animated WebP never inspects a frame's bitstream, so this is documented as always false.
        Assert.False(info.IsLosslessEncoding);
    }

    private static ImageInfo EncodeThenIdentify(Image source, WebpEncoderOptions options)
    {
        using var ms = new MemoryStream();
        source.Save(ms, "webp", options);
        ms.Position = 0;
        return Image.Identify(ms);
    }

    private static Image CreateSolidColorImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 180;
            pixels[i + 1] = 90;
            pixels[i + 2] = 40;
        }

        return image;
    }
}
