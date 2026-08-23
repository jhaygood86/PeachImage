namespace PeachImage.Tests.Resizing;

public class ImageResizeCropTests
{
    [Theory]
    [InlineData(400, 300, 100, 100)] // landscape source, square target
    [InlineData(300, 400, 100, 100)] // portrait source, square target
    [InlineData(400, 400, 100, 100)] // square source, square target
    [InlineData(50, 50, 200, 200)] // upscale — Crop can grow the source, unlike Max
    [InlineData(50, 25, 200, 200)] // upscale, non-matching aspect ratio
    public void Resize_ModeCrop_AlwaysProducesExactlyTheRequestedDimensions(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var source = Image.Create(sourceWidth, sourceHeight, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var cropped = source.Resize(targetWidth, targetHeight, new ResizeOptions { Mode = ResizeMode.Crop });

        Assert.Equal(targetWidth, cropped.Width);
        Assert.Equal(targetHeight, cropped.Height);
    }

    [Theory]
    [InlineData(AnchorPosition.TopLeft)]
    [InlineData(AnchorPosition.TopCenter)]
    [InlineData(AnchorPosition.TopRight)]
    [InlineData(AnchorPosition.MiddleLeft)]
    [InlineData(AnchorPosition.MiddleCenter)]
    [InlineData(AnchorPosition.MiddleRight)]
    [InlineData(AnchorPosition.BottomLeft)]
    [InlineData(AnchorPosition.BottomCenter)]
    [InlineData(AnchorPosition.BottomRight)]
    public void Resize_ModeCrop_AnchorControlsHorizontalSlack(AnchorPosition anchor)
    {
        // 40x20 source into a 10x10 target: cover-scale is 0.25, giving a 10x5 fill — wait, needs both axes
        // covered. Use a source whose cover-scale leaves slack only on the horizontal axis, so the vertical
        // offset is always 0 and every anchor's horizontal placement can be checked independently.
        var source = CreateIndexedImage(40, 20);
        var options = new ResizeOptions { Mode = ResizeMode.Crop, Anchor = anchor, Filter = ResamplingFilter.NearestNeighbor };

        var cropped = source.Resize(10, 10, options);
        var full = source.Resize(20, 10, new ResizeOptions { Filter = ResamplingFilter.NearestNeighbor }); // the cover-scaled 20x10 intermediate

        int expectedOffsetX = ExpectedOffset(anchor, horizontal: true, slack: 10);
        AssertRegionMatches(full, cropped, expectedOffsetX, offsetY: 0);
    }

    [Theory]
    [InlineData(AnchorPosition.TopLeft)]
    [InlineData(AnchorPosition.TopCenter)]
    [InlineData(AnchorPosition.TopRight)]
    [InlineData(AnchorPosition.MiddleLeft)]
    [InlineData(AnchorPosition.MiddleCenter)]
    [InlineData(AnchorPosition.MiddleRight)]
    [InlineData(AnchorPosition.BottomLeft)]
    [InlineData(AnchorPosition.BottomCenter)]
    [InlineData(AnchorPosition.BottomRight)]
    public void Resize_ModeCrop_AnchorControlsVerticalSlack(AnchorPosition anchor)
    {
        // Transpose of the horizontal case: slack only on the vertical axis.
        var source = CreateIndexedImage(20, 40);
        var options = new ResizeOptions { Mode = ResizeMode.Crop, Anchor = anchor, Filter = ResamplingFilter.NearestNeighbor };

        var cropped = source.Resize(10, 10, options);
        var full = source.Resize(10, 20, new ResizeOptions { Filter = ResamplingFilter.NearestNeighbor }); // the cover-scaled 10x20 intermediate

        int expectedOffsetY = ExpectedOffset(anchor, horizontal: false, slack: 10);
        AssertRegionMatches(full, cropped, offsetX: 0, expectedOffsetY);
    }

    [Fact]
    public void Resize_ModeCrop_ReturnsSameInstance_WhenAlreadyExactlyTheRequestedSize()
    {
        var source = Image.Create(50, 50, PixelFormat.Rgba32);
        FillWithRandomBytes(source);

        var cropped = source.Resize(50, 50, new ResizeOptions { Mode = ResizeMode.Crop });

        Assert.Same(source, cropped);
    }

    [Fact]
    public void Resize_ThrowsForNonPositiveDimensions_ModeCrop()
    {
        var source = Image.Create(4, 4, PixelFormat.Rgb24);

        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(0, 4, new ResizeOptions { Mode = ResizeMode.Crop }));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(4, 0, new ResizeOptions { Mode = ResizeMode.Crop }));
    }

    private static int ExpectedOffset(AnchorPosition anchor, bool horizontal, int slack)
    {
        string name = anchor.ToString();
        if (horizontal)
        {
            if (name.EndsWith("Left", StringComparison.Ordinal))
            {
                return 0;
            }

            if (name.EndsWith("Right", StringComparison.Ordinal))
            {
                return slack;
            }

            return slack / 2;
        }

        if (name.StartsWith("Top", StringComparison.Ordinal))
        {
            return 0;
        }

        if (name.StartsWith("Bottom", StringComparison.Ordinal))
        {
            return slack;
        }

        return slack / 2;
    }

    private static void AssertRegionMatches(Image full, Image cropped, int offsetX, int offsetY)
    {
        for (int y = 0; y < cropped.Height; y++)
        {
            var expectedRow = full.GetRowSpan(offsetY + y).Slice(offsetX * 4, cropped.Width * 4);
            var actualRow = cropped.GetRowSpan(y);
            Assert.True(expectedRow.SequenceEqual(actualRow), $"Row {y} did not match the expected cover-scaled region at offset ({offsetX}, {offsetY}).");
        }
    }

    // Rgba32 image where each pixel encodes its own (x, y) coordinates, so nearest-neighbor-resized copies
    // remain directly comparable to a manually-sliced sub-rectangle of an independently resized reference.
    private static Image CreateIndexedImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 4;
                row[offset] = (byte)(x % 256);
                row[offset + 1] = (byte)(y % 256);
                row[offset + 2] = (byte)((x + y) % 256);
                row[offset + 3] = 255;
            }
        }

        return image;
    }

    private static void FillWithRandomBytes(Image image)
    {
        var random = new Random(image.Width * 31 + image.Height);
        var span = image.GetPixelSpan();
        random.NextBytes(span);
    }
}
