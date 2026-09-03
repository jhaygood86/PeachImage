using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif;

/// <summary>
/// Regression guard for the bug where lossless AVIF output was larger than the source PNG on real
/// photographic content (root-caused to chroma always using DC_PRED -- see <c>SearchUvMode</c> in
/// <c>Av1TileEncoder</c> -- despite lossless chroma being full-resolution 4:4:4 carrying as much real detail
/// as luma, plus the partition-tree RDO search being disabled entirely). Unlike <c>EncodeDecodeRoundTripTests</c>
/// (which only checks pixel fidelity), this compares byte counts against a PNG encode of the same pixels, the
/// actual comparison the original bug report was about.
///
/// <para>Uses a synthetic image rather than a checked-in real photo to avoid any licensing question over a
/// third-party image. Getting a synthetic pattern that's actually representative took real trial and error
/// (see <see cref="CreateFractalNoiseImage"/>'s remarks) -- both a pure smooth gradient and pure independent
/// per-pixel noise turned out to be poor proxies (confirmed AVIF loses to PNG on both, even in ffmpeg's own
/// reference AV1 encoder, so asserting AVIF-beats-PNG against either would be a false positive, not a real
/// regression guard). Multi-octave value noise (summed random grids at decreasing cell size and amplitude --
/// the classic "fractal"/"pink" noise construction) has a spatial-frequency falloff much closer to a real
/// photograph's, and empirically produces the same size relationship a real photo does.</para>
///
/// <para>Manually verified against a real photograph (a 1054x1492 flyer PNG) during development: before this
/// fix, lossless AVIF was 102.0% of the source PNG's size (2,336,568 vs 2,291,209 bytes -- the literal
/// reported bug); after, 93.4% (2,139,972 bytes), against ffmpeg's own lossless AVIF encode at 81.3%
/// (1,863,221 bytes) as an external reference point for how much further headroom remains.</para>
/// </summary>
public class LosslessSizeRegressionTests
{
    [Theory]
    [InlineData(128, 128, 1)]
    [InlineData(150, 200, 2)]
    [InlineData(256, 256, 3)]
    public void FractalNoiseImage_LosslessAvif_IsSmallerThanSourcePng(int width, int height, int seed)
    {
        using var image = CreateFractalNoiseImage(width, height, seed);

        using var pngStream = new MemoryStream();
        image.Save(pngStream, "png");

        using var avifStream = new MemoryStream();
        image.Save(avifStream, "avif", new AvifEncoderOptions { Lossless = true });

        Assert.True(
            avifStream.Length < pngStream.Length,
            $"Lossless AVIF ({avifStream.Length} bytes) was not smaller than the source PNG ({pngStream.Length} bytes) for a {width}x{height} photo-like image.");
    }

    /// <summary>
    /// Three octaves of bilinearly-upsampled value noise (independent random values on a coarse grid,
    /// smoothly interpolated, summed at decreasing cell size and amplitude per octave) per RGB channel --
    /// natural-looking mottled color variation with detail at multiple scales, the way a real photo's spatial
    /// frequencies fall off from coarse (broad lighting/color regions) to fine (texture, grain) rather than
    /// being concentrated at one scale. Deterministic for a given <paramref name="seed"/>.
    /// </summary>
    private static Image CreateFractalNoiseImage(int width, int height, int seed)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        var rng = new Random(seed);

        int[] cellSizes = [Math.Max(4, width / 4), Math.Max(2, width / 16), Math.Max(1, width / 64)];
        double[] amplitudes = [90, 45, 20];

        var layersR = new double[cellSizes.Length][,];
        var layersG = new double[cellSizes.Length][,];
        var layersB = new double[cellSizes.Length][,];
        for (int i = 0; i < cellSizes.Length; i++)
        {
            layersR[i] = ValueNoiseLayer(rng, width, height, cellSizes[i]);
            layersG[i] = ValueNoiseLayer(rng, width, height, cellSizes[i]);
            layersB[i] = ValueNoiseLayer(rng, width, height, cellSizes[i]);
        }

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 3;
                double r = 30, g = 40, b = 60;
                for (int i = 0; i < cellSizes.Length; i++)
                {
                    r += (layersR[i][row, col] - 0.5) * 2 * amplitudes[i];
                    g += (layersG[i][row, col] - 0.5) * 2 * amplitudes[i];
                    b += (layersB[i][row, col] - 0.5) * 2 * amplitudes[i];
                }

                pixels[idx + 0] = (byte)Math.Clamp(r, 0, 255);
                pixels[idx + 1] = (byte)Math.Clamp(g, 0, 255);
                pixels[idx + 2] = (byte)Math.Clamp(b, 0, 255);
            }
        }

        return image;
    }

    /// <summary>One octave of value noise: independent random values on a <paramref name="cellSize"/>-spaced grid, bilinearly interpolated up to full <paramref name="width"/>x<paramref name="height"/> resolution.</summary>
    private static double[,] ValueNoiseLayer(Random rng, int width, int height, int cellSize)
    {
        int gridWidth = (width / cellSize) + 2;
        int gridHeight = (height / cellSize) + 2;
        var grid = new double[gridHeight, gridWidth];
        for (int y = 0; y < gridHeight; y++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                grid[y, x] = rng.NextDouble();
            }
        }

        var result = new double[height, width];
        for (int row = 0; row < height; row++)
        {
            double fy = (double)row / cellSize;
            int y0 = (int)fy;
            double ty = fy - y0;
            for (int col = 0; col < width; col++)
            {
                double fx = (double)col / cellSize;
                int x0 = (int)fx;
                double tx = fx - x0;
                double v00 = grid[y0, x0];
                double v10 = grid[y0, x0 + 1];
                double v01 = grid[y0 + 1, x0];
                double v11 = grid[y0 + 1, x0 + 1];
                double top = v00 + ((v10 - v00) * tx);
                double bottom = v01 + ((v11 - v01) * tx);
                result[row, col] = top + ((bottom - top) * ty);
            }
        }

        return result;
    }
}
