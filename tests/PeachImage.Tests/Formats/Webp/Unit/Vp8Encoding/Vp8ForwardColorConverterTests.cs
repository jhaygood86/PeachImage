using PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;
using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8Encoding;

/// <summary>
/// Validates <see cref="Vp8ForwardColorConverter"/> by round-tripping through the real, unmodified
/// <see cref="Vp8ScalarColorConverter"/> (YUV-&gt;RGB). For flat-color images chroma subsampling loses nothing
/// (every 2x2 block averages to the same color), so the round-trip should be tight; for detailed content some
/// chroma loss is expected and only luma (never subsampled) is held to a tight tolerance.
/// </summary>
public class Vp8ForwardColorConverterTests
{
    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(128, 128, 128)]
    [InlineData(255, 255, 255)]
    [InlineData(0, 0, 0)]
    [InlineData(37, 201, 88)]
    public void ConvertPlanes_FlatColor_RoundTripsWithinSmallTolerance(byte r, byte g, byte b)
    {
        const int width = 8;
        const int height = 8;
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            rgb[(i * 3) + 0] = r;
            rgb[(i * 3) + 1] = g;
            rgb[(i * 3) + 2] = b;
        }

        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        byte[] yPlane = new byte[width * height];
        byte[] uPlane = new byte[chromaWidth * chromaHeight];
        byte[] vPlane = new byte[chromaWidth * chromaHeight];

        Vp8ForwardColorConverter.ConvertPlanes(rgb, width, height, yPlane, width, uPlane, vPlane, chromaWidth);

        for (int y = 0; y < height; y++)
        {
            byte[] yRow = new byte[width];
            byte[] uRow = new byte[width];
            byte[] vRow = new byte[width];
            for (int x = 0; x < width; x++)
            {
                yRow[x] = yPlane[(y * width) + x];
                int cx = x / 2;
                int cy = y / 2;
                uRow[x] = uPlane[(cy * chromaWidth) + cx];
                vRow[x] = vPlane[(cy * chromaWidth) + cx];
            }

            byte[] outRow = new byte[width * 3];
            new Vp8ScalarColorConverter().ConvertRow(yRow, uRow, vRow, outRow, width);

            for (int x = 0; x < width; x++)
            {
                Assert.True(Math.Abs(outRow[(x * 3) + 0] - r) <= 3, $"R at ({x},{y}): expected ~{r}, got {outRow[(x * 3) + 0]}.");
                Assert.True(Math.Abs(outRow[(x * 3) + 1] - g) <= 3, $"G at ({x},{y}): expected ~{g}, got {outRow[(x * 3) + 1]}.");
                Assert.True(Math.Abs(outRow[(x * 3) + 2] - b) <= 3, $"B at ({x},{y}): expected ~{b}, got {outRow[(x * 3) + 2]}.");
            }
        }
    }

    [Fact]
    public void ConvertPlanes_Gradient_LumaRoundTripsWithinTolerance()
    {
        const int width = 16;
        const int height = 16;
        byte[] rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                rgb[(i * 3) + 0] = (byte)(x * 16);
                rgb[(i * 3) + 1] = (byte)(y * 16);
                rgb[(i * 3) + 2] = (byte)((x + y) * 8);
            }
        }

        int chromaWidth = (width + 1) / 2;
        byte[] yPlane = new byte[width * height];
        byte[] uPlane = new byte[chromaWidth * ((height + 1) / 2)];
        byte[] vPlane = new byte[chromaWidth * ((height + 1) / 2)];

        Vp8ForwardColorConverter.ConvertPlanes(rgb, width, height, yPlane, width, uPlane, vPlane, chromaWidth);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                byte expectedY = Vp8ForwardColorConverter.ConvertY(rgb[(i * 3) + 0], rgb[(i * 3) + 1], rgb[(i * 3) + 2]);
                Assert.Equal(expectedY, yPlane[(y * width) + x]);
            }
        }
    }

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(10, 200, 40)]
    public void ConvertY_MatchesInverseFormulaWithinRounding(byte r, byte g, byte b)
    {
        byte y = Vp8ForwardColorConverter.ConvertY(r, g, b);

        // Feeding this Y back through the real YUV->RGB converter alongside neutral (gray) chroma should
        // reproduce roughly the source's luminance -- a loose sanity check that the fixed-point scale lines up
        // with Vp8ScalarColorConverter's own constants, not a precise round-trip (chroma is discarded here).
        byte[] outRgb = new byte[3];
        Vp8ScalarColorConverter.ConvertPixel(y, 128, 128, outRgb);

        int expectedLuma = (int)Math.Round((0.299 * r) + (0.587 * g) + (0.114 * b));
        int actualLuma = (int)Math.Round((0.299 * outRgb[0]) + (0.587 * outRgb[1]) + (0.114 * outRgb[2]));
        Assert.True(Math.Abs(expectedLuma - actualLuma) <= 4, $"Expected luma ~{expectedLuma}, got {actualLuma}.");
    }
}
