using PeachImage.Formats.Jxl.Encoder.VarDct;
using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Tests.Formats.Jxl.Unit;

/// <summary>Sharpening by the encoder followed by the decoder's Gaborish smoothing must give back the original planes.</summary>
public class JxlGaborishTests
{
    [Theory]
    [InlineData(40, 24)]
    [InlineData(17, 9)]
    [InlineData(64, 64)]
    public void Sharpen_IsInvertedByTheDecodersSmoothing(int width, int height)
    {
        int stride = (width + 7) & ~7;
        int paddedHeight = (height + 7) & ~7;
        var random = new Random(width * height);
        var original = new float[3][];
        var planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            original[c] = new float[stride * paddedHeight];
            for (int y = 0; y < paddedHeight; y++)
            {
                for (int x = 0; x < stride; x++)
                {
                    int sy = Math.Min(y, height - 1);
                    int sx = Math.Min(x, width - 1);
                    original[c][(y * stride) + x] = (float)((0.4 * Math.Sin((sx * 0.5) + c)) + (0.3 * Math.Cos(sy * 0.4)) + (0.05 * (((sy * 31) + (sx * 17)) % 7)));
                }
            }

            // The decoder swaps pooled buffers in and out of the array it filters, so the planes must come from the pool.
            planes[c] = System.Buffers.ArrayPool<float>.Shared.Rent(original[c].Length);
            original[c].CopyTo(planes[c], 0);
        }

        JxlGaborish.Sharpen(planes, stride, width, height, paddedHeight);
        RestorationFilters.Apply(planes, stride, width, height, JxlLoopFilter.Create(gaborish: true, epfIterations: 0), null, stride / 8);

        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.True(Math.Abs(original[c][(y * stride) + x] - planes[c][(y * stride) + x]) < 0.01f, $"Channel {c} ({x},{y})");
                }
            }
        }
    }
}
