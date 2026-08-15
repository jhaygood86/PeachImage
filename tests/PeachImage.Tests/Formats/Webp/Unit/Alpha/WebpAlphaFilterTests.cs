using PeachImage.Formats.Webp.Decoding.Alpha;

namespace PeachImage.Tests.Formats.Webp.Unit.Alpha;

public class WebpAlphaFilterTests
{
    private static readonly byte[] Original4x3 =
    [
        10, 20, 30, 40,
        200, 150, 100, 50,
        5, 250, 128, 64,
    ];

    [Fact]
    public void None_LeavesPlaneUnchanged()
    {
        byte[] plane = (byte[])Original4x3.Clone();

        WebpAlphaFilter.Reverse(WebpAlphaFilterMethod.None, plane, 4, 3);

        Assert.Equal(Original4x3, plane);
    }

    [Fact]
    public void Horizontal_RoundTripsForwardFilteredPlane()
    {
        byte[] filtered = ForwardHorizontal(Original4x3, 4, 3);

        WebpAlphaFilter.Reverse(WebpAlphaFilterMethod.Horizontal, filtered, 4, 3);

        Assert.Equal(Original4x3, filtered);
    }

    [Fact]
    public void Vertical_RoundTripsForwardFilteredPlane()
    {
        byte[] filtered = ForwardVertical(Original4x3, 4, 3);

        WebpAlphaFilter.Reverse(WebpAlphaFilterMethod.Vertical, filtered, 4, 3);

        Assert.Equal(Original4x3, filtered);
    }

    [Fact]
    public void Gradient_RoundTripsForwardFilteredPlane()
    {
        byte[] filtered = ForwardGradient(Original4x3, 4, 3);

        WebpAlphaFilter.Reverse(WebpAlphaFilterMethod.Gradient, filtered, 4, 3);

        Assert.Equal(Original4x3, filtered);
    }

    [Fact]
    public void Horizontal_FirstRow_MatchesHandComputedValues()
    {
        // Row 0, predictor starts at 0: filtered[0] = 10 - 0 = 10; filtered[1] = 20 - 10 = 10;
        // filtered[2] = 30 - 20 = 10; filtered[3] = 40 - 30 = 10.
        byte[] filtered = ForwardHorizontal(Original4x3, 4, 3);
        Assert.Equal(new byte[] { 10, 10, 10, 10 }, filtered[..4]);
    }

    [Fact]
    public void Vertical_RowZero_UsesHorizontalFallbackNotZeroPredictor()
    {
        // Row 0 has no row above, so Vertical falls back to the horizontal (chained) reconstruction for
        // that row, matching libwebp's VerticalUnfilter_C(prev=NULL, ...) behavior — NOT "add 0" per pixel.
        byte[] filtered = ForwardVertical(Original4x3, 4, 3);
        byte[] expectedRow0 = ForwardHorizontal(Original4x3, 4, 3)[..4];
        Assert.Equal(expectedRow0, filtered[..4]);
    }

    [Fact]
    public void Gradient_RowZero_UsesHorizontalFallbackNotZeroNeighbors()
    {
        byte[] filtered = ForwardGradient(Original4x3, 4, 3);
        byte[] expectedRow0 = ForwardHorizontal(Original4x3, 4, 3)[..4];
        Assert.Equal(expectedRow0, filtered[..4]);
    }

    // --- Forward-filter reference helpers (mirror image of WebpAlphaFilter.Reverse, used only by tests) ---

    private static byte[] ForwardHorizontal(byte[] original, int width, int height)
    {
        byte[] result = new byte[original.Length];
        for (int y = 0; y < height; y++)
        {
            byte predictor = y == 0 ? (byte)0 : original[(y - 1) * width];
            for (int x = 0; x < width; x++)
            {
                byte source = original[(y * width) + x];
                result[(y * width) + x] = (byte)(source - predictor);
                predictor = source;
            }
        }

        return result;
    }

    private static byte[] ForwardVertical(byte[] original, int width, int height)
    {
        byte[] result = new byte[original.Length];

        // Row 0 falls back to horizontal-with-zero-predictor, same as the decode-side reversal.
        byte predictor = 0;
        for (int x = 0; x < width; x++)
        {
            result[x] = (byte)(original[x] - predictor);
            predictor = original[x];
        }

        for (int y = 1; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                result[(y * width) + x] = (byte)(original[(y * width) + x] - original[((y - 1) * width) + x]);
            }
        }

        return result;
    }

    private static byte[] ForwardGradient(byte[] original, int width, int height)
    {
        byte[] result = new byte[original.Length];

        byte predictor = 0;
        for (int x = 0; x < width; x++)
        {
            result[x] = (byte)(original[x] - predictor);
            predictor = original[x];
        }

        for (int y = 1; y < height; y++)
        {
            byte left = original[(y - 1) * width];
            byte topLeft = original[(y - 1) * width];
            for (int x = 0; x < width; x++)
            {
                byte top = original[((y - 1) * width) + x];
                int predicted = Math.Clamp(left + top - topLeft, 0, 255);
                byte source = original[(y * width) + x];
                result[(y * width) + x] = (byte)(source - predicted);
                left = source;
                topLeft = top;
            }
        }

        return result;
    }
}
