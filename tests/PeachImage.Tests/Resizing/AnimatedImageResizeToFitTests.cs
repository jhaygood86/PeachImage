namespace PeachImage.Tests.Resizing;

public class AnimatedImageResizeToFitTests
{
    [Fact]
    public void ResizeToFit_ScalesDownToFitBounds_PreservingAspectRatio_ForEveryFrame()
    {
        var frames = new List<AnimatedImageFrame>
        {
            new(MakeFrame(400, 200), TimeSpan.FromMilliseconds(100), FrameDisposalMethod.None),
            new(MakeFrame(400, 200), TimeSpan.FromMilliseconds(250), FrameDisposalMethod.RestoreToBackground),
        };
        var animated = new AnimatedImage(frames, 400, 200, loopCount: 3);

        var fitted = animated.ResizeToFit(100, 100);

        Assert.Equal(100, fitted.Width);
        Assert.Equal(50, fitted.Height);
        Assert.Equal(3, fitted.LoopCount);

        foreach (var frame in fitted.Frames)
        {
            Assert.Equal(100, frame.Image.Width);
            Assert.Equal(50, frame.Image.Height);
        }
    }

    [Fact]
    public void ResizeToFit_DoesNotUpscale_ReturnsSameInstance_WhenCanvasAlreadyFits()
    {
        var frames = new List<AnimatedImageFrame>
        {
            new(MakeFrame(50, 30), TimeSpan.FromMilliseconds(100), FrameDisposalMethod.None),
        };
        var animated = new AnimatedImage(frames, 50, 30, loopCount: 0);

        var fitted = animated.ResizeToFit(100, 100);

        Assert.Same(animated, fitted);
    }

    [Fact]
    public void ResizeToFit_SingleMaxDimensionOverload_MatchesExplicitSquareBox()
    {
        var frames = new List<AnimatedImageFrame> { new(MakeFrame(400, 200), TimeSpan.FromMilliseconds(100), FrameDisposalMethod.None) };
        var animated = new AnimatedImage(frames, 400, 200, loopCount: 0);

        var viaSingleArg = animated.ResizeToFit(100);
        var viaExplicitBox = animated.ResizeToFit(100, 100);

        Assert.Equal(viaExplicitBox.Width, viaSingleArg.Width);
        Assert.Equal(viaExplicitBox.Height, viaSingleArg.Height);
    }

    [Fact]
    public void ResizeToFit_IsLazy_DoesNotResizeUntilFramesAreEnumerated()
    {
        int resizedCount = 0;
        IEnumerable<AnimatedImageFrame> CountingFrames()
        {
            for (int i = 0; i < 2; i++)
            {
                resizedCount++;
                yield return new AnimatedImageFrame(MakeFrame(400, 200), TimeSpan.FromMilliseconds(10), FrameDisposalMethod.None);
            }
        }

        var animated = new AnimatedImage(CountingFrames(), 400, 200, loopCount: 0);
        var fitted = animated.ResizeToFit(100, 100);

        Assert.Equal(0, resizedCount);

        using var enumerator = fitted.Frames.GetEnumerator();
        enumerator.MoveNext();
        Assert.Equal(1, resizedCount);
    }

    [Fact]
    public void ResizeToFit_ThrowsForNonPositiveBounds()
    {
        var animated = new AnimatedImage([new AnimatedImageFrame(MakeFrame(4, 4), TimeSpan.Zero, FrameDisposalMethod.None)], 4, 4, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => animated.ResizeToFit(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => animated.ResizeToFit(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => animated.ResizeToFit(0));
    }

    private static Image MakeFrame(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        new Random(width * 17 + height).NextBytes(image.GetPixelSpan());
        return image;
    }
}
