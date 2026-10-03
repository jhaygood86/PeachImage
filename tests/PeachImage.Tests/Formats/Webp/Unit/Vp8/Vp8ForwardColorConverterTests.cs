using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// <see cref="Vp8ForwardColorConverter.ConvertPlanes"/> processes whole groups of 16 pixels on vectors and the
/// rest (row tails, odd last row/column) in scalar. These sizes straddle every seam; the reference is the
/// per-sample <c>ConvertY/U/V</c> formulas with clamp-to-edge 2x2 sums.
/// </summary>
public class Vp8ForwardColorConverterTests
{
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(15, 3, 0)]
    [InlineData(16, 2, 0)]
    [InlineData(16, 3, 1)]
    [InlineData(17, 5, 0)]
    [InlineData(31, 4, 1)]
    [InlineData(32, 7, 0)]
    [InlineData(33, 6, 2)]
    [InlineData(48, 3, 0)]
    [InlineData(130, 9, 1)]
    [InlineData(257, 4, 2)]
    public void ConvertPlanes_MatchesPerSampleFormulas(int width, int height, int pattern)
    {
        var rng = new Random((width * 131) + height);
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i++)
        {
            rgb[i] = pattern switch
            {
                0 => (byte)rng.Next(256),
                1 => rng.Next(2) == 0 ? (byte)0 : (byte)255, // Saturating extremes.
                _ => (byte)(rng.Next(4) == 0 ? 255 : rng.Next(0, 8)),
            };
        }

        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        int yStride = width + 5; // Strides wider than the image, as the encoder's padded planes have.
        int uvStride = chromaWidth + 3;

        var y = new byte[yStride * height];
        var u = new byte[uvStride * chromaHeight];
        var v = new byte[uvStride * chromaHeight];
        Vp8ForwardColorConverter.ConvertPlanes(rgb, width, height, y, yStride, u, v, uvStride);

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int o = ((row * width) + col) * 3;
                Assert.Equal(Vp8ForwardColorConverter.ConvertY(rgb[o], rgb[o + 1], rgb[o + 2]), y[(row * yStride) + col]);
            }
        }

        for (int cy = 0; cy < chromaHeight; cy++)
        {
            for (int cx = 0; cx < chromaWidth; cx++)
            {
                int y0 = cy * 2, y1 = Math.Min(y0 + 1, height - 1);
                int x0 = cx * 2, x1 = Math.Min(x0 + 1, width - 1);
                int r = 0, g = 0, b = 0;
                foreach (var (yy, xx) in new[] { (y0, x0), (y0, x1), (y1, x0), (y1, x1) })
                {
                    int o = ((yy * width) + xx) * 3;
                    r += rgb[o];
                    g += rgb[o + 1];
                    b += rgb[o + 2];
                }

                Assert.Equal(Vp8ForwardColorConverter.ConvertU(r, g, b), u[(cy * uvStride) + cx]);
                Assert.Equal(Vp8ForwardColorConverter.ConvertV(r, g, b), v[(cy * uvStride) + cx]);
            }
        }
    }
}
