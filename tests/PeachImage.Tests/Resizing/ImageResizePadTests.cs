using PeachImage.Formats.Shared.Compositing;

namespace PeachImage.Tests.Resizing;

public class ImageResizePadTests
{
    [Theory]
    [InlineData(400, 300, 100, 100)] // landscape source, square target
    [InlineData(300, 400, 100, 100)] // portrait source, square target
    [InlineData(50, 50, 200, 200)] // upscale — a case Max would've left unchanged (50 <= 200 on both axes)
    [InlineData(50, 25, 200, 200)] // upscale, non-matching aspect ratio
    public void Resize_ModePad_AlwaysProducesExactlyTheRequestedDimensions(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var source = Image.Create(sourceWidth, sourceHeight, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var padded = source.Resize(targetWidth, targetHeight, new ResizeOptions { Mode = ResizeMode.Pad });

        Assert.Equal(targetWidth, padded.Width);
        Assert.Equal(targetHeight, padded.Height);
    }

    [Fact]
    public void Resize_ModePad_UpscalesUnlikeMax()
    {
        var source = Image.Create(50, 50, PixelFormat.Rgba32);
        FillWithRandomBytes(source);

        var viaMax = source.Resize(200, 200, new ResizeOptions { Mode = ResizeMode.Max });
        var viaPad = source.Resize(200, 200, new ResizeOptions { Mode = ResizeMode.Pad });

        Assert.Same(source, viaMax); // Max: shrink-only, source already fits, returned unchanged
        Assert.NotSame(source, viaPad);
        Assert.Equal(200, viaPad.Width);
        Assert.Equal(200, viaPad.Height);
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
    public void Resize_ModePad_AnchorControlsPlacement(AnchorPosition anchor)
    {
        // 20x10 source into a 10x10 target: fit-scale is 0.5, giving a 10x5 fit — slack only on the
        // vertical axis, so every anchor's vertical placement can be checked independently.
        var source = CreateIndexedImage(20, 10);
        var options = new ResizeOptions
        {
            Mode = ResizeMode.Pad,
            Anchor = anchor,
            Filter = ResamplingFilter.NearestNeighbor,
            BackgroundColor = (10, 20, 30, 255),
        };

        var padded = source.Resize(10, 10, options);
        var fitted = source.Resize(10, 5, new ResizeOptions { Filter = ResamplingFilter.NearestNeighbor }); // the fit-scaled 10x5 intermediate

        int expectedOffsetY = ExpectedOffset(anchor, horizontal: false, slack: 5);

        for (int y = 0; y < 10; y++)
        {
            var row = padded.GetRowSpan(y);
            if (y >= expectedOffsetY && y < expectedOffsetY + 5)
            {
                var expectedRow = fitted.GetRowSpan(y - expectedOffsetY);
                Assert.True(expectedRow.SequenceEqual(row), $"Row {y} did not match the fit-scaled source at offset {expectedOffsetY}.");
            }
            else
            {
                for (int x = 0; x < 10; x++)
                {
                    Assert.Equal((byte)10, row[(x * 4) + 0]);
                    Assert.Equal((byte)20, row[(x * 4) + 1]);
                    Assert.Equal((byte)30, row[(x * 4) + 2]);
                    Assert.Equal((byte)255, row[(x * 4) + 3]);
                }
            }
        }
    }

    [Theory]
    [InlineData(PixelFormat.Gray8)]
    [InlineData(PixelFormat.Rgb24)]
    [InlineData(PixelFormat.Rgba32)]
    [InlineData(PixelFormat.Cmyk32)]
    [InlineData(PixelFormat.Gray16)]
    [InlineData(PixelFormat.Rgb48)]
    [InlineData(PixelFormat.Rgba64)]
    public void Resize_ModePad_DefaultBackgroundColor_IsWhiteForOpaqueFormats_TransparentForAlphaFormats(PixelFormat format)
    {
        var source = Image.Create(20, 10, format);
        FillWithRandomBytes(source);

        var padded = source.Resize(10, 10, new ResizeOptions { Mode = ResizeMode.Pad, Filter = ResamplingFilter.NearestNeighbor });

        var (r, g, b, a) = ImageFramer.ResolveBackgroundColor(null, format);
        int bytesPerPixel = format.GetBytesPerPixel();
        Span<byte> expected = stackalloc byte[8];
        PixelFormatFill.EncodeColor(format, r, g, b, a, expected[..bytesPerPixel]);

        // With a 20x10 source fit into a 10x10 target, the fit-scaled source is 10x5 — the top row is
        // always background regardless of anchor's default (MiddleCenter centers a 5px-tall fit within 10px,
        // leaving row 0 as border either way).
        var topRow = padded.GetRowSpan(0)[..bytesPerPixel];
        Assert.True(expected[..bytesPerPixel].SequenceEqual(topRow));
    }

    [Fact]
    public void Resize_ModePad_ReturnsSameInstance_WhenAlreadyExactlyTheRequestedSize()
    {
        var source = Image.Create(50, 50, PixelFormat.Rgba32);
        FillWithRandomBytes(source);

        var padded = source.Resize(50, 50, new ResizeOptions { Mode = ResizeMode.Pad });

        Assert.Same(source, padded);
    }

    [Fact]
    public void Resize_ThrowsForNonPositiveDimensions_ModePad()
    {
        var source = Image.Create(4, 4, PixelFormat.Rgb24);

        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(0, 4, new ResizeOptions { Mode = ResizeMode.Pad }));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(4, 0, new ResizeOptions { Mode = ResizeMode.Pad }));
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
