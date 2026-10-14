using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.VarDct;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.Encoder.VarDct;

/// <summary>
/// The encoder-side inverse of the decoder's Gaborish smoothing. The decoder blurs the reconstructed XYB planes with a fixed
/// 3x3 kernel (centre, four cross taps, four diagonal taps, mirrored at the image border); sharpening the planes by that
/// kernel's inverse before the DCT makes the pair a near no-op on clean content while the blur still softens the block
/// edges that quantization introduces.
/// </summary>
internal static class JxlGaborish
{
    // Richardson iteration x += t - G x converges at |1 - eigenvalue| per step; the default kernel's smallest eigenvalue is ~0.46,
    // so eight steps leave under 1% of the original error.
    private const int Iterations = 8;

    /// <summary>
    /// Replaces <paramref name="planes"/> (each <paramref name="stride"/> wide) with the planes the decoder's Gaborish maps back to
    /// the originals over the real <paramref name="width"/> x <paramref name="height"/> area, then re-extends the padding beyond it.
    /// </summary>
    public static void Sharpen(float[][] planes, int stride, int width, int height, int paddedHeight)
    {
        var weights = new JxlLoopFilter().GabWeights;
        JxlParallel.For(planes.Length, c =>
        {
            float w1 = weights[c * 2];
            float w2 = weights[(c * 2) + 1];
            float norm = 1.0f / (1.0f + (4 * (w1 + w2)));
            SharpenPlane(planes[c], stride, width, height, norm, w1 * norm, w2 * norm);
            ExtendPadding(planes[c], stride, width, height, paddedHeight);
        });
    }

    private static void SharpenPlane(float[] plane, int stride, int width, int height, float centre, float cross, float diagonal)
    {
        var target = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            plane.AsSpan(y * stride, width).CopyTo(target.AsSpan(y * width, width));
        }

        var current = (float[])target.Clone();
        var blurred = new float[width * height];
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            RowParallel.For(height, y => BlurRow(current, blurred, width, height, y, centre, cross, diagonal));
            for (int i = 0; i < current.Length; i++)
            {
                current[i] += target[i] - blurred[i];
            }
        }

        for (int y = 0; y < height; y++)
        {
            current.AsSpan(y * width, width).CopyTo(plane.AsSpan(y * stride, width));
        }
    }

    private static void BlurRow(float[] source, float[] destination, int width, int height, int y, float centre, float cross, float diagonal)
    {
        int up = FramePostProcessing.Mirror(y - 1, height) * width;
        int middle = y * width;
        int down = FramePostProcessing.Mirror(y + 1, height) * width;
        for (int x = 0; x < width; x++)
        {
            int left = FramePostProcessing.Mirror(x - 1, width);
            int right = FramePostProcessing.Mirror(x + 1, width);
            float sum1 = source[middle + left] + source[middle + right] + source[up + x] + source[down + x];
            float sum2 = source[up + left] + source[up + right] + source[down + left] + source[down + right];
            destination[middle + x] = (sum2 * diagonal) + ((sum1 * cross) + (source[middle + x] * centre));
        }
    }

    private static void ExtendPadding(float[] plane, int stride, int width, int height, int paddedHeight)
    {
        for (int y = 0; y < height; y++)
        {
            float edge = plane[(y * stride) + width - 1];
            plane.AsSpan((y * stride) + width, stride - width).Fill(edge);
        }

        for (int y = height; y < paddedHeight; y++)
        {
            plane.AsSpan((height - 1) * stride, stride).CopyTo(plane.AsSpan(y * stride, stride));
        }
    }
}
