using PeachImage.Tests.Formats.Jxl.Unit;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlAnimationTests
{
    [Fact]
    public void Animation_ReportsFramesTimingAndLoops()
    {
        byte[] jxl = JxlTestAssets.Load("conformance_animation_spline.jxl");
        Assert.True(Image.Identify(new MemoryStream(jxl)).IsAnimated);

        var animation = AnimatedImage.Load(new MemoryStream(jxl));
        var frames = animation.Frames.Select(f => (f.Duration, f.Disposal, f.Image.Width, f.Image.Height)).ToList();

        Assert.True(frames.Count > 1);
        Assert.All(frames, f =>
        {
            Assert.True(f.Duration > TimeSpan.Zero);
            Assert.Equal(FrameDisposalMethod.None, f.Disposal);
            Assert.Equal(animation.Width, f.Width);
            Assert.Equal(animation.Height, f.Height);
        });
        Assert.True(animation.LoopCount >= 0);
    }

    [Fact]
    public void ImageLoad_OfAnAnimation_ReturnsTheFirstFrame()
    {
        byte[] jxl = JxlTestAssets.Load("conformance_animation_spline.jxl");

        using var still = Image.Load(new MemoryStream(jxl), new DecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 });
        var first = AnimatedImage.Load(new MemoryStream(jxl)).Frames.First();

        Assert.Equal(first.Image.GetPixelSpan().ToArray(), still.GetPixelSpan().ToArray());
    }

    [Fact]
    public void AStillImage_IsAOneFrameAnimation()
    {
        var animation = AnimatedImage.Load(new MemoryStream(JxlTestAssets.Load("rgb_lossless.jxl")));

        var frames = animation.Frames.ToList();

        Assert.Single(frames);
        Assert.Equal(PixelFormat.Rgba32, frames[0].Image.PixelFormat);
    }
}
