using System.Buffers;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Jxl.Modular;
using PeachImage.Formats.Jxl.VarDct;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.Frame;

/// <summary>Helpers for Modular frames whose colour planes are JPEG-style YCbCr, possibly chroma-subsampled.</summary>
internal static class JxlYCbCrModular
{
    /// <summary>The horizontal shift of a chroma subsampling mode (0: none, 1: 2x1, 2: 1x2 (4:4:0), 3: 4x1).</summary>
    public static int HorizontalShift(int mode) => mode switch { 1 => 1, 2 => 1, _ => 0 };

    /// <summary>The vertical shift of a chroma subsampling mode.</summary>
    public static int VerticalShift(int mode) => mode switch { 1 => 1, 3 => 1, _ => 0 };

    /// <summary>
    /// Converts one colour channel into a rented float plane of <paramref name="stride"/> x <paramref name="rows"/> floats,
    /// upsampling it to <paramref name="width"/> x <paramref name="height"/> when it is stored at a smaller size.
    /// </summary>
    public static float[] ToPlane(ModularChannel channel, JxlBitDepth depth, int stride, int rows, int width, int height)
    {
        var pool = ArrayPool<float>.Shared;
        if (channel.Width == width && channel.Height == height)
        {
            var plane = pool.Rent(stride * rows);
            if (stride == width)
            {
                JxlSampleConversion.ToFloatPlane(channel.Array, plane, width, height, depth);
            }
            else
            {
                var compact = pool.Rent(width * height);
                JxlSampleConversion.ToFloatPlane(channel.Array, compact, width, height, depth);
                RowParallel.For(height, y => Array.Copy(compact, y * width, plane, y * stride, width));
                pool.Return(compact);
            }

            return plane;
        }

        int hShift = channel.HShift;
        int vShift = channel.VShift;
        var reduced = pool.Rent(channel.Width * channel.Height);
        JxlSampleConversion.ToFloatPlane(channel.Array, reduced, channel.Width, channel.Height, depth);
        float[] upsampled = ChromaUpsampling.Upsample(reduced, channel.Width, channel.Width, channel.Height, hShift, vShift, stride, rows);
        pool.Return(reduced);
        return upsampled;
    }
}
