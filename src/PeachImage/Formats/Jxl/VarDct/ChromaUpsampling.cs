using System.Buffers;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>
/// Upsamples subsampled chroma planes (JPEG-style 4:2:0, 4:2:2, 4:4:0, 4:1:1) to full resolution with the format's "triangle"
/// filter: each output sample is 3/4 of the nearest input sample plus 1/4 of its neighbour on the side the output lies, mirrored at the plane's edges.
/// </summary>
internal static class ChromaUpsampling
{
    /// <summary>
    /// Returns a rented plane of <paramref name="dstStride"/> x <paramref name="dstRows"/> floats holding the upsampled
    /// <paramref name="source"/> (whose real size is <paramref name="width"/> x <paramref name="height"/> samples, rows
    /// <paramref name="srcStride"/> apart). The caller returns <paramref name="source"/> to the pool.
    /// </summary>
    public static float[] Upsample(float[] source, int srcStride, int width, int height, int hShift, int vShift, int dstStride, int dstRows)
    {
        var pool = ArrayPool<float>.Shared;
        float[] current = source;
        int currentStride = srcStride;
        float[]? intermediate = null;

        if (hShift != 0)
        {
            float[] output = pool.Rent(dstStride * dstRows);
            int inStride = currentStride;
            float[] input = current;
            RowParallel.For(height, y =>
            {
                int inRow = y * inStride;
                int outRow = y * dstStride;
                for (int x = 0; x < width; x++)
                {
                    float cur = input[inRow + x] * 0.75f;
                    float prev = input[inRow + Math.Max(x - 1, 0)];
                    float next = input[inRow + Math.Min(x + 1, width - 1)];
                    output[outRow + (2 * x)] = (0.25f * prev) + cur;
                    output[outRow + (2 * x) + 1] = (0.25f * next) + cur;
                }
            });

            current = output;
            currentStride = dstStride;
            intermediate = output;
            width *= 2;
        }

        if (vShift != 0)
        {
            float[] output = pool.Rent(dstStride * dstRows);
            int inStride = currentStride;
            float[] input = current;
            int columns = width;
            RowParallel.For(height, y =>
            {
                int top = Math.Max(y - 1, 0) * inStride;
                int middle = y * inStride;
                int bottom = Math.Min(y + 1, height - 1) * inStride;
                int out0 = 2 * y * dstStride;
                int out1 = out0 + dstStride;
                for (int x = 0; x < columns; x++)
                {
                    float mid = input[middle + x] * 0.75f;
                    output[out0 + x] = (0.25f * input[top + x]) + mid;
                    output[out1 + x] = (0.25f * input[bottom + x]) + mid;
                }
            });

            if (intermediate is not null)
            {
                pool.Return(intermediate);
            }

            current = output;
        }

        return current;
    }
}
