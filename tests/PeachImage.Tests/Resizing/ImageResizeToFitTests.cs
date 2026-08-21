namespace PeachImage.Tests.Resizing;

public class ImageResizeToFitTests
{
    [Theory]
    [InlineData(400, 300, 100, 100, 100, 75)] // landscape, width-constrained
    [InlineData(300, 400, 100, 100, 75, 100)] // portrait, height-constrained
    [InlineData(400, 400, 100, 100, 100, 100)] // square box, square source
    [InlineData(1000, 250, 200, 200, 200, 50)] // very wide, width-constrained
    [InlineData(250, 1000, 200, 200, 50, 200)] // very tall, height-constrained
    public void Resize_ModeMax_ScalesDownToLargestSizeWithinBounds_PreservingAspectRatio(
        int sourceWidth, int sourceHeight, int maxWidth, int maxHeight, int expectedWidth, int expectedHeight)
    {
        var source = Image.Create(sourceWidth, sourceHeight, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var fitted = source.Resize(maxWidth, maxHeight, new ResizeOptions { Mode = ResizeMode.Max });

        Assert.Equal(expectedWidth, fitted.Width);
        Assert.Equal(expectedHeight, fitted.Height);
        Assert.True(fitted.Width <= maxWidth);
        Assert.True(fitted.Height <= maxHeight);
    }

    [Theory]
    [InlineData(50, 50, 100, 100)] // smaller than box on both axes
    [InlineData(100, 100, 100, 100)] // exactly the box on both axes
    [InlineData(100, 50, 100, 100)] // exactly the box on one axis, smaller on the other
    public void Resize_ModeMax_DoesNotUpscale_ReturnsSameInstance_WhenSourceAlreadyFits(
        int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        var source = Image.Create(sourceWidth, sourceHeight, PixelFormat.Rgba32);
        FillWithRandomBytes(source);

        var fitted = source.Resize(maxWidth, maxHeight, new ResizeOptions { Mode = ResizeMode.Max });

        Assert.Same(source, fitted);
    }

    [Fact]
    public void Resize_ModeExact_IsTheDefault_AndAlwaysProducesTheExactRequestedDimensions()
    {
        var source = Image.Create(50, 50, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        // Without Mode = Max, a source that already "fits" a larger box is still resized exactly, not
        // returned unchanged — Exact ignores aspect ratio and the source's current size entirely.
        var resized = source.Resize(100, 100);

        Assert.NotSame(source, resized);
        Assert.Equal(100, resized.Width);
        Assert.Equal(100, resized.Height);
    }

    [Fact]
    public void Resize_ModeMax_UsesRequestedFilter_SameAsModeExactWithComputedDimensions()
    {
        var source = Image.Create(400, 300, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var viaMax = source.Resize(100, 100, new ResizeOptions { Filter = ResamplingFilter.Lanczos3, Mode = ResizeMode.Max });
        var viaExact = source.Resize(100, 75, new ResizeOptions { Filter = ResamplingFilter.Lanczos3 });

        Assert.Equal(viaExact.Width, viaMax.Width);
        Assert.Equal(viaExact.Height, viaMax.Height);
        Assert.Equal(viaExact.GetPixelSpan().ToArray(), viaMax.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Resize_ModeMax_SquareBoundingBox_BothDimensionsShareOneMaxArgument()
    {
        var source = Image.Create(400, 300, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var viaTwoArgs = source.Resize(150, 150, new ResizeOptions { Mode = ResizeMode.Max });

        Assert.True(viaTwoArgs.Width <= 150);
        Assert.True(viaTwoArgs.Height <= 150);
    }

    [Fact]
    public void Resize_ThrowsForNonPositiveDimensions_RegardlessOfMode()
    {
        var source = Image.Create(4, 4, PixelFormat.Rgb24);

        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(-1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(0, 4, new ResizeOptions { Mode = ResizeMode.Max }));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(4, 0, new ResizeOptions { Mode = ResizeMode.Max }));
    }

    private static void FillWithRandomBytes(Image image)
    {
        var random = new Random(image.Width * 31 + image.Height);
        var span = image.GetPixelSpan();
        random.NextBytes(span);
    }
}
