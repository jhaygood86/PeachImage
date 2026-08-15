using PeachImage.Formats.Webp.Decoding.Vp8.Upsampling;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Pins the row-at-a-time chroma upsampler against the per-pixel <c>Sample</c> form the decoder used to call
/// once per output pixel per plane.
/// </summary>
/// <remarks>
/// The row form hoists the row coordinates and peels the column clamp out of the loop, so the cases that can
/// break are all at the edges: the first and last output pixel of a row, odd widths where the final pixel has
/// no partner, and chroma planes narrower than the output row implies. Every combination of small output width
/// and chroma width is enumerated rather than sampled.
/// </remarks>
public class Vp8ChromaUpsamplerRowTests
{
    [Fact]
    public void UpsampleRow_MatchesPerPixelSample_AcrossEveryWidthAndRowParity()
    {
        var random = new Random(31337);

        for (int chromaWidth = 1; chromaWidth <= 17; chromaWidth++)
        {
            const int ChromaHeight = 9;
            int stride = chromaWidth + 3; // deliberately padded, as the real planes are.

            byte[] plane = new byte[stride * ChromaHeight];
            random.NextBytes(plane);

            for (int width = 1; width <= 2 * chromaWidth; width++)
            {
                for (int outY = 0; outY < 2 * ChromaHeight; outY++)
                {
                    int nearRow = outY >> 1;
                    int farRow = Clamp((outY >> 1) + (((outY & 1) == 1) ? 1 : -1), ChromaHeight - 1);

                    byte[] actual = new byte[width];
                    Vp8ChromaUpsampler.UpsampleRow(
                        plane.AsSpan(nearRow * stride, chromaWidth),
                        plane.AsSpan(farRow * stride, chromaWidth),
                        actual,
                        width,
                        chromaWidth);

                    for (int x = 0; x < width; x++)
                    {
                        byte expected = Vp8ChromaUpsampler.Sample(plane, stride, chromaWidth, ChromaHeight, x, outY);
                        Assert.Equal(expected, actual[x]);
                    }
                }
            }
        }
    }

    /// <summary>Constant chroma must upsample to that same constant everywhere — the rounding has to be exact, not merely close.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(254)]
    [InlineData(255)]
    public void UpsampleRow_OfAConstantPlane_ReproducesThatConstant(byte value)
    {
        const int ChromaWidth = 9;
        const int Width = 17;

        byte[] row = new byte[ChromaWidth];
        Array.Fill(row, value);

        byte[] actual = new byte[Width];
        Vp8ChromaUpsampler.UpsampleRow(row, row, actual, Width, ChromaWidth);

        Assert.All(actual, sample => Assert.Equal(value, sample));
    }

    private static int Clamp(int v, int max) => v < 0 ? 0 : v > max ? max : v;
}
