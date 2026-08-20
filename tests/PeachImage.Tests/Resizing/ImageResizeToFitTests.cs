namespace PeachImage.Tests.Resizing;

public class ImageResizeToFitTests
{
    [Theory]
    [InlineData(400, 300, 100, 100, 100, 75)] // landscape, width-constrained
    [InlineData(300, 400, 100, 100, 75, 100)] // portrait, height-constrained
    [InlineData(400, 400, 100, 100, 100, 100)] // square box, square source
    [InlineData(1000, 250, 200, 200, 200, 50)] // very wide, width-constrained
    [InlineData(250, 1000, 200, 200, 50, 200)] // very tall, height-constrained
    public void ResizeToFit_ScalesDownToLargestSizeWithinBounds_PreservingAspectRatio(
        int sourceWidth, int sourceHeight, int maxWidth, int maxHeight, int expectedWidth, int expectedHeight)
    {
        var source = Image.Create(sourceWidth, sourceHeight, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var fitted = source.ResizeToFit(maxWidth, maxHeight);

        Assert.Equal(expectedWidth, fitted.Width);
        Assert.Equal(expectedHeight, fitted.Height);
        Assert.True(fitted.Width <= maxWidth);
        Assert.True(fitted.Height <= maxHeight);
    }

    [Theory]
    [InlineData(50, 50, 100, 100)] // smaller than box on both axes
    [InlineData(100, 100, 100, 100)] // exactly the box on both axes
    [InlineData(100, 50, 100, 100)] // exactly the box on one axis, smaller on the other
    public void ResizeToFit_DoesNotUpscale_ReturnsSameInstance_WhenSourceAlreadyFits(
        int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        var source = Image.Create(sourceWidth, sourceHeight, PixelFormat.Rgba32);
        FillWithRandomBytes(source);

        var fitted = source.ResizeToFit(maxWidth, maxHeight);

        Assert.Same(source, fitted);
    }

    [Fact]
    public void ResizeToFit_SingleMaxDimensionOverload_MatchesExplicitSquareBox()
    {
        var source = Image.Create(400, 300, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var viaSingleArg = source.ResizeToFit(150);
        var viaExplicitBox = source.ResizeToFit(150, 150);

        Assert.Equal(viaExplicitBox.Width, viaSingleArg.Width);
        Assert.Equal(viaExplicitBox.Height, viaSingleArg.Height);
        Assert.Equal(viaExplicitBox.GetPixelSpan().ToArray(), viaSingleArg.GetPixelSpan().ToArray());
    }

    [Fact]
    public void ResizeToFit_UsesRequestedFilter_SameAsCallingResizeDirectly()
    {
        var source = Image.Create(400, 300, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var options = new ResizeOptions { Filter = ResamplingFilter.Lanczos3 };
        var viaResizeToFit = source.ResizeToFit(100, 100, options);
        var viaResize = source.Resize(100, 75, options);

        Assert.Equal(viaResize.Width, viaResizeToFit.Width);
        Assert.Equal(viaResize.Height, viaResizeToFit.Height);
        Assert.Equal(viaResize.GetPixelSpan().ToArray(), viaResizeToFit.GetPixelSpan().ToArray());
    }

    [Fact]
    public void ResizeToFit_ThrowsForNonPositiveBounds()
    {
        var source = Image.Create(10, 10, PixelFormat.Rgb24);

        Assert.Throws<ArgumentOutOfRangeException>(() => source.ResizeToFit(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ResizeToFit(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ResizeToFit(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ResizeToFit(0));
    }

    private static void FillWithRandomBytes(Image image)
    {
        var random = new Random(image.Width * 31 + image.Height);
        var span = image.GetPixelSpan();
        random.NextBytes(span);
    }
}
