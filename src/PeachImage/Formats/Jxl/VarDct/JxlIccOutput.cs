using System.Numerics;
using PeachImage.Formats.Shared.Parallelism;
using PeachImage.Internal.Icc;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>
/// Converts the linear-sRGB result of decoding an XYB-coded image into the device space of the image's embedded ICC
/// profile (relative colorimetric), so that the pixels agree with the profile that is attached to the image.
/// </summary>
internal static class JxlIccOutput
{
    /// <summary>Opens <paramref name="profile"/>, which must be an RGB profile (or a gray profile when <paramref name="gray"/> is set).</summary>
    public static IccProfile OpenProfile(byte[] profile, bool gray)
    {
        IccProfile icc;
        try
        {
            icc = new IccProfile(profile);
            icc.ErrorIfUnsupported();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new JxlUnsupportedFeatureException("The embedded ICC profile of an XYB-coded image is not supported: " + ex.Message);
        }

        if (icc.ChannelCount != (gray ? 1 : 3))
        {
            throw new JxlUnsupportedFeatureException("The ICC profile of an XYB-coded image does not match its colour channels.");
        }

        return icc;
    }

    // The ICC engine throws assorted exceptions on damaged profiles (and lazily, from inside the conversion); report them as one.
    private static void Guarded(Action conversion)
    {
        try
        {
            conversion();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or JxlFormatException))
        {
            var cause = ex is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : ex;
            throw new JxlDecodingException("The embedded ICC profile is damaged: " + cause.Message);
        }
    }

    /// <summary>Converts the luminance in the first plane (linear light) to the gray profile's device values, in place.</summary>
    public static void FromLinearGray(IccProfile profile, float[][] planes, int stride, int width, int height) =>
        Guarded(() => FromLinearGrayCore(profile, planes, stride, width, height));

    private static void FromLinearGrayCore(IccProfile profile, float[][] planes, int stride, int width, int height)
    {
        // A neutral of luminance Y has the D50 white point scaled by Y in the profile connection space.
        var white = IccColorMath.XyzD50ToLinearSrgb.Inverse().Multiply(new IccVector3(1, 1, 1));
        if (profile.TryGetFromXyzGreyTrc(IccIntent.RelativeColorimetric, out double whiteY, out double[] inverseTable))
        {
            // A gray TRC profile is a single tone-curve lookup of the luminance.
            float[] lut = ToFloatTable(inverseTable);
            float scale = (float)(white.Y / whiteY);
            RowParallel.For(height, y =>
            {
                int row = y * stride;
                float[] plane = planes[0];
                for (int x = 0; x < width; x++)
                {
                    plane[row + x] = Lookup(lut, plane[row + x] * scale);
                }
            });
            return;
        }

        Parallel.For(0, height, y =>
        {
            Span<double> device = stackalloc double[1];
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                double luminance = planes[0][row + x];
                profile.FromXyzD50(new IccVector3(white.X * luminance, white.Y * luminance, white.Z * luminance), IccIntent.RelativeColorimetric, device);
                planes[0][row + x] = (float)Math.Clamp(device[0], 0.0, 1.0);
            }
        });
    }

    /// <summary>Converts three linear-sRGB planes to the profile's device values in place.</summary>
    public static void FromLinearSrgb(IccProfile profile, float[][] planes, int stride, int width, int height) =>
        Guarded(() => FromLinearSrgbCore(profile, planes, stride, width, height));

    private static void FromLinearSrgbCore(IccProfile profile, float[][] planes, int stride, int width, int height)
    {
        var toXyz = IccColorMath.XyzD50ToLinearSrgb.Inverse();
        if (profile.TryGetFromXyzMatrixTrc(IccIntent.RelativeColorimetric, out var fromXyz, out double[][] inverseTables) && inverseTables.Length == 3)
        {
            MatrixTrc(planes, stride, width, height, fromXyz, toXyz, inverseTables);
            return;
        }

        Parallel.For(0, height, y =>
        {
            Span<double> device = stackalloc double[3];
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                var xyz = toXyz.Multiply(new IccVector3(planes[0][row + x], planes[1][row + x], planes[2][row + x]));
                profile.FromXyzD50(xyz, IccIntent.RelativeColorimetric, device);
                for (int c = 0; c < 3; c++)
                {
                    planes[c][row + x] = (float)Math.Clamp(device[c], 0.0, 1.0);
                }
            }
        });
    }

    // RGB matrix/TRC profile: linear sRGB -> D50 XYZ -> linear device RGB is one 3x3 matrix, then each channel goes through its
    // inverse tone-curve table (the same 2048-entry tables and linear interpolation the general engine uses).
    private static void MatrixTrc(float[][] planes, int stride, int width, int height, IccMatrix3x3 fromXyz, IccMatrix3x3 toXyz, double[][] inverseTables)
    {
        var combined = new float[9];
        for (int column = 0; column < 3; column++)
        {
            var unit = new IccVector3(column == 0 ? 1 : 0, column == 1 ? 1 : 0, column == 2 ? 1 : 0);
            var result = fromXyz.Multiply(toXyz.Multiply(unit));
            combined[column] = (float)result.X;
            combined[3 + column] = (float)result.Y;
            combined[6 + column] = (float)result.Z;
        }

        float[][] luts = [ToFloatTable(inverseTables[0]), ToFloatTable(inverseTables[1]), ToFloatTable(inverseTables[2])];
        RowParallel.For(height, y =>
        {
            int row = y * stride;
            float[] p0 = planes[0];
            float[] p1 = planes[1];
            float[] p2 = planes[2];
            int x = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var m = new Vector<float>[9];
                for (int i = 0; i < 9; i++)
                {
                    m[i] = new Vector<float>(combined[i]);
                }

                for (; x <= width - Vector<float>.Count; x += Vector<float>.Count)
                {
                    int i = row + x;
                    var r = new Vector<float>(p0, i);
                    var g = new Vector<float>(p1, i);
                    var b = new Vector<float>(p2, i);
                    ((m[0] * r) + (m[1] * g) + (m[2] * b)).CopyTo(p0, i);
                    ((m[3] * r) + (m[4] * g) + (m[5] * b)).CopyTo(p1, i);
                    ((m[6] * r) + (m[7] * g) + (m[8] * b)).CopyTo(p2, i);
                }
            }

            for (; x < width; x++)
            {
                int i = row + x;
                float r = p0[i];
                float g = p1[i];
                float b = p2[i];
                p0[i] = (combined[0] * r) + (combined[1] * g) + (combined[2] * b);
                p1[i] = (combined[3] * r) + (combined[4] * g) + (combined[5] * b);
                p2[i] = (combined[6] * r) + (combined[7] * g) + (combined[8] * b);
            }

            for (x = 0; x < width; x++)
            {
                int i = row + x;
                p0[i] = Lookup(luts[0], p0[i]);
                p1[i] = Lookup(luts[1], p1[i]);
                p2[i] = Lookup(luts[2], p2[i]);
            }
        });
    }

    private static float[] ToFloatTable(double[] table)
    {
        var result = new float[table.Length];
        for (int i = 0; i < table.Length; i++)
        {
            result[i] = (float)table[i];
        }

        return result;
    }

    // Linear interpolation in the table with the input clamped to [0, 1]; the result is clamped to [0, 1] as well.
    private static float Lookup(float[] table, float value)
    {
        if (!(value > 0f))
        {
            return Math.Clamp(table[0], 0f, 1f);
        }

        if (value >= 1f)
        {
            return Math.Clamp(table[^1], 0f, 1f);
        }

        float exact = value * (table.Length - 1);
        int lower = (int)exact;
        float distance = exact - lower;
        float low = table[lower];
        return Math.Clamp(low + ((table[lower + 1] - low) * distance), 0f, 1f);
    }
}
