namespace PeachImage.Tests.Resizing;

public class AnimatedImageResizeTests
{
    [Fact]
    public void Resize_PreservesFrameCount_Timing_AndDisposal_ForEveryFrame()
    {
        var frames = new List<AnimatedImageFrame>
        {
            new(MakeFrame(8, 6), TimeSpan.FromMilliseconds(100), FrameDisposalMethod.None),
            new(MakeFrame(8, 6), TimeSpan.FromMilliseconds(250), FrameDisposalMethod.RestoreToBackground),
            new(MakeFrame(8, 6), TimeSpan.FromMilliseconds(50), FrameDisposalMethod.RestoreToPrevious),
        };
        var animated = new AnimatedImage(frames, 8, 6, loopCount: 3);

        var resized = animated.Resize(4, 3, new ResizeOptions { Filter = ResamplingFilter.Bilinear });

        Assert.Equal(4, resized.Width);
        Assert.Equal(3, resized.Height);
        Assert.Equal(3, resized.LoopCount);

        var resizedFrames = resized.Frames.ToList();
        Assert.Equal(frames.Count, resizedFrames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal(4, resizedFrames[i].Image.Width);
            Assert.Equal(3, resizedFrames[i].Image.Height);
            Assert.Equal(frames[i].Duration, resizedFrames[i].Duration);
            Assert.Equal(frames[i].Disposal, resizedFrames[i].Disposal);
        }
    }

    [Theory]
    [InlineData(ResamplingFilter.Bicubic)]
    [InlineData(ResamplingFilter.Lanczos3)]
    [InlineData(ResamplingFilter.NearestNeighbor)]
    public void Resize_MatchesPerFrameIndependentResize_ByteForByte(ResamplingFilter filter)
    {
        // AnimatedImage.Resize builds the horizontal/vertical weight maps once and reuses them across every
        // frame (see AnimatedImage.ResizeFrames), instead of calling Image.Resize independently per frame
        // (which rebuilds them each time). A cached weight map and a freshly-built one for the same
        // (sourceSize, destSize, filter) are the same deterministic floating-point computation, so this
        // asserts the two paths produce byte-identical output — the caching is a pure performance change,
        // not an observable one.
        var sourceFrames = new List<Image> { MakeFrame(10, 6), MakeFrame(10, 6), MakeFrame(10, 6) };
        var animated = new AnimatedImage(
            sourceFrames.Select(image => new AnimatedImageFrame(image, TimeSpan.FromMilliseconds(100), FrameDisposalMethod.None)),
            10, 6, loopCount: 0);

        var options = new ResizeOptions { Filter = filter };
        var cachedPathFrames = animated.Resize(4, 5, options).Frames.ToList();

        Assert.Equal(sourceFrames.Count, cachedPathFrames.Count);
        for (int i = 0; i < sourceFrames.Count; i++)
        {
            var independentlyResized = sourceFrames[i].Resize(4, 5, options);
            Assert.Equal(independentlyResized.GetPixelSpan().ToArray(), cachedPathFrames[i].Image.GetPixelSpan().ToArray());
        }
    }

    [Fact]
    public void Resize_IsLazy_DoesNotResizeUntilFramesAreEnumerated()
    {
        int resizedCount = 0;
        IEnumerable<AnimatedImageFrame> CountingFrames()
        {
            for (int i = 0; i < 3; i++)
            {
                resizedCount++;
                yield return new AnimatedImageFrame(MakeFrame(4, 4), TimeSpan.FromMilliseconds(10), FrameDisposalMethod.None);
            }
        }

        var animated = new AnimatedImage(CountingFrames(), 4, 4, loopCount: 0);
        var resized = animated.Resize(2, 2);

        Assert.Equal(0, resizedCount);

        using var enumerator = resized.Frames.GetEnumerator();
        enumerator.MoveNext();
        Assert.Equal(1, resizedCount);
        enumerator.MoveNext();
        Assert.Equal(2, resizedCount);
    }

    [Fact]
    public void Resize_ThrowsForNonPositiveDimensions()
    {
        var animated = new AnimatedImage([new AnimatedImageFrame(MakeFrame(4, 4), TimeSpan.Zero, FrameDisposalMethod.None)], 4, 4, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => animated.Resize(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => animated.Resize(4, 0));
    }

    private static Image MakeFrame(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        new Random(width * 17 + height).NextBytes(image.GetPixelSpan());
        return image;
    }
}
