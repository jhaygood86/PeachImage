namespace PeachImage.Tests.Resizing;

public class AnimatedImageResizeCropTests
{
    [Theory]
    [InlineData(ResamplingFilter.Bicubic, AnchorPosition.MiddleCenter)]
    [InlineData(ResamplingFilter.Lanczos3, AnchorPosition.TopLeft)]
    [InlineData(ResamplingFilter.NearestNeighbor, AnchorPosition.BottomRight)]
    public void Resize_ModeCrop_MatchesPerFrameIndependentResize_ByteForByte(ResamplingFilter filter, AnchorPosition anchor)
    {
        // Same cached-weight-map-vs-independent-per-frame parity check as AnimatedImageResizeTests, extended
        // to Crop: the shared FramingPlan is computed once per AnimatedImage.Resize call (see ResizeFrames),
        // not once per frame — this pins that the result is still identical to resizing each frame alone.
        var sourceFrames = new List<Image> { MakeFrame(20, 12), MakeFrame(20, 12), MakeFrame(20, 12) };
        var animated = new AnimatedImage(
            sourceFrames.Select(image => new AnimatedImageFrame(image, TimeSpan.FromMilliseconds(100), FrameDisposalMethod.None)),
            20, 12, loopCount: 0);

        var options = new ResizeOptions { Mode = ResizeMode.Crop, Filter = filter, Anchor = anchor };
        var cachedPathFrames = animated.Resize(8, 8, options).Frames.ToList();

        Assert.Equal(sourceFrames.Count, cachedPathFrames.Count);
        for (int i = 0; i < sourceFrames.Count; i++)
        {
            var independentlyResized = sourceFrames[i].Resize(8, 8, options);
            Assert.Equal(independentlyResized.GetPixelSpan().ToArray(), cachedPathFrames[i].Image.GetPixelSpan().ToArray());
        }
    }

    [Fact]
    public void Resize_ModeCrop_PreservesFrameCount_Timing_AndDisposal()
    {
        var frames = new List<AnimatedImageFrame>
        {
            new(MakeFrame(20, 12), TimeSpan.FromMilliseconds(100), FrameDisposalMethod.None),
            new(MakeFrame(20, 12), TimeSpan.FromMilliseconds(250), FrameDisposalMethod.RestoreToBackground),
        };
        var animated = new AnimatedImage(frames, 20, 12, loopCount: 2);

        var resized = animated.Resize(8, 8, new ResizeOptions { Mode = ResizeMode.Crop });
        var resizedFrames = resized.Frames.ToList();

        Assert.Equal(frames.Count, resizedFrames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal(8, resizedFrames[i].Image.Width);
            Assert.Equal(8, resizedFrames[i].Image.Height);
            Assert.Equal(frames[i].Duration, resizedFrames[i].Duration);
            Assert.Equal(frames[i].Disposal, resizedFrames[i].Disposal);
        }
    }

    [Fact]
    public void Resize_ModeCrop_ReturnsSameInstance_WhenAlreadyExactlyTheRequestedSize()
    {
        var animated = new AnimatedImage([new AnimatedImageFrame(MakeFrame(6, 6), TimeSpan.Zero, FrameDisposalMethod.None)], 6, 6, 0);

        var resized = animated.Resize(6, 6, new ResizeOptions { Mode = ResizeMode.Crop });

        Assert.Same(animated, resized);
    }

    private static Image MakeFrame(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        new Random(width * 17 + height).NextBytes(image.GetPixelSpan());
        return image;
    }
}
