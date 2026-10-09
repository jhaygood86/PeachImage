using System.Numerics;
using System.Runtime.InteropServices;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.Frame;

/// <summary>Bulk conversion between integer Modular samples and normalized float samples (vectorized, row-parallel).</summary>
internal static class JxlSampleConversion
{
    /// <summary>The factor that normalizes an integer sample of <paramref name="bitsPerSample"/> bits to [0, 1].</summary>
    public static float NormalizationScale(int bitsPerSample) => (float)(1.0 / (double)((1UL << bitsPerSample) - 1));

    /// <summary><c>destination[i] = source[i] * scale</c>.</summary>
    public static void ToFloat(ReadOnlySpan<int> source, Span<float> destination, float scale)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var s = new Vector<float>(scale);
            for (; i <= source.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                (Vector.ConvertToSingle(new Vector<int>(source[i..])) * s).CopyTo(destination[i..]);
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] = source[i] * scale;
        }
    }

    /// <summary><c>destination[i] = clamp(trunc(source[i] * max + 0.5), 0, max)</c> -- round to nearest, ties up.</summary>
    public static void ToInt(ReadOnlySpan<float> source, Span<int> destination, float max)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var m = new Vector<float>(max);
            var half = new Vector<float>(0.5f);
            var zero = Vector<float>.Zero;
            for (; i <= source.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var v = Vector.Min(Vector.Max((new Vector<float>(source[i..]) * m) + half, zero), m + half);
                Vector.ConvertToInt32(v).CopyTo(destination[i..]);
            }
        }

        for (; i < source.Length; i++)
        {
            float v = Math.Min(Math.Max((source[i] * max) + 0.5f, 0f), max + 0.5f);
            destination[i] = (int)v;
        }
    }

    /// <summary>Converts a whole channel to a compact float plane, rows in parallel.</summary>
    public static void ToFloatPlane(int[] source, float[] destination, int width, int height, float scale)
    {
        RowParallel.For(height, y => ToFloat(source.AsSpan(y * width, width), destination.AsSpan(y * width, width), scale));
    }

    /// <summary>Converts <paramref name="height"/> rows of a float plane (rows <paramref name="sourceStride"/> apart) into a compact integer channel.</summary>
    public static void ToIntChannel(float[] source, int sourceStride, int[] destination, int width, int height, float max)
    {
        RowParallel.For(height, y => ToInt(source.AsSpan(y * sourceStride, width), destination.AsSpan(y * width, width), max));
    }

    /// <summary>
    /// Converts a whole channel of the given bit depth to a float plane: integer samples are normalized to [0, 1], custom-float
    /// samples are decoded to their actual values.
    /// </summary>
    public static void ToFloatPlane(int[] source, float[] destination, int width, int height, JxlBitDepth depth)
    {
        if (!depth.IsFloat)
        {
            ToFloatPlane(source, destination, width, height, NormalizationScale((int)depth.BitsPerSample));
            return;
        }

        var (bits, exponentBits) = JxlCustomFloat.Parameters(depth);
        RowParallel.For(height, y =>
        {
            var src = source.AsSpan(y * width, width);
            var dst = destination.AsSpan(y * width, width);
            for (int x = 0; x < width; x++)
            {
                dst[x] = JxlCustomFloat.Decode(src[x], bits, exponentBits);
            }
        });
    }

    /// <summary>The inverse of <see cref="ToFloatPlane(int[], float[], int, int, JxlBitDepth)"/>: rows of a float plane become a compact channel.</summary>
    public static void ToIntChannel(float[] source, int sourceStride, int[] destination, int width, int height, JxlBitDepth depth)
    {
        if (!depth.IsFloat)
        {
            ToIntChannel(source, sourceStride, destination, width, height, (float)((1UL << (int)depth.BitsPerSample) - 1));
            return;
        }

        var (bits, exponentBits) = JxlCustomFloat.Parameters(depth);
        RowParallel.For(height, y =>
        {
            var src = source.AsSpan(y * sourceStride, width);
            var dst = destination.AsSpan(y * width, width);
            for (int x = 0; x < width; x++)
            {
                dst[x] = JxlCustomFloat.Encode(src[x], bits, exponentBits);
            }
        });
    }
}
