using System.Buffers;
using System.Runtime.InteropServices;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Internal.PixelFormatConversion;

/// <summary>
/// Converts an in-memory <see cref="Image"/> between the gray / RGB / RGBA pixel formats at 8-bit, 16-bit and 32-bit float
/// depth. The 8-bit pairs use the vectorized kernels; everything else goes through straight (non-premultiplied) float RGBA
/// one row at a time. Values are converted as stored: no transfer function or gamut conversion is applied.
/// </summary>
internal static class ImagePixelConverter
{
    private const float LumaR = 0.299f;
    private const float LumaG = 0.587f;
    private const float LumaB = 0.114f;

    public static bool IsConvertible(PixelFormat format) => format is not (PixelFormat.Cmyk32 or PixelFormat.Ycck32);

    /// <summary>A per-row hook that runs on a row of straight, interleaved float RGBA between reading the source and writing the target.</summary>
    public delegate void RowTransform(float[] rgba, int width);

    /// <summary>Converts <paramref name="source"/> to <paramref name="target"/>, applying <paramref name="transform"/> to every row in between (never uses the 8-bit fast paths).</summary>
    public static Image ConvertWithTransform(Image source, PixelFormat target, ParallelSettings parallel, RowTransform? transform, bool unpremultiply = true)
    {
        var result = Image.Create(source.Width, source.Height, target);
        try
        {
            ConvertGeneric(source, result, source.PixelFormat, target, parallel, transform, unpremultiply);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether <paramref name="from"/> can be converted to <paramref name="to"/>. Ink-based sources (CMYK, YCCK) convert only to
    /// RGB, RGBA and gray, using the profile-less formula of <see cref="InkToRgba32"/>; nothing converts into them.
    /// </summary>
    public static bool CanConvert(PixelFormat from, PixelFormat to) =>
        from is PixelFormat.Cmyk32 or PixelFormat.Ycck32
            ? to is PixelFormat.Rgb24 or PixelFormat.Rgba32 or PixelFormat.Gray8
            : IsConvertible(from) && IsConvertible(to);

    /// <summary>
    /// Converts 8-bit <see cref="PixelFormat.Cmyk32"/> (or raw <see cref="PixelFormat.Ycck32"/>) to opaque Rgba32 with no colour profile:
    /// <c>R = 255 x (1 - C) x (1 - K)</c>, likewise G and B, where the channels are scaled to 0..1 (0 is no ink). An approximation
    /// of how the inks would look, not a colorimetric conversion.
    /// </summary>
    public static Image InkToRgba32(Image source)
    {
        var result = Image.Create(source.Width, source.Height, PixelFormat.Rgba32);
        try
        {
            var src = source.GetPixelSpan();
            var dst = result.GetPixelSpan();
            int count = source.Width * source.Height;
            bool ycck = source.PixelFormat == PixelFormat.Ycck32;
            for (int i = 0; i < count; i++)
            {
                int s = i * 4;
                int c, m, y, k = src[s + 3];
                if (ycck)
                {
                    (byte r, byte g, byte b) = PeachImage.Formats.Jpeg.ColorConversion.ScalarColorConverter.ConvertYCbCrPixel(src[s], src[s + 1], src[s + 2]);
                    c = 255 - r;
                    m = 255 - g;
                    y = 255 - b;
                }
                else
                {
                    c = src[s];
                    m = src[s + 1];
                    y = src[s + 2];
                }

                int invK = 255 - k;
                dst[s] = Scale(255 - c, invK);
                dst[s + 1] = Scale(255 - m, invK);
                dst[s + 2] = Scale(255 - y, invK);
                dst[s + 3] = 255;
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    // a * b / 255, rounded to nearest.
    private static byte Scale(int a, int b) => (byte)(((a * b) + 127) / 255);

    public static Image Convert(Image source, PixelFormat target, ParallelSettings parallel = default)
    {
        if (source.PixelFormat is PixelFormat.Cmyk32 or PixelFormat.Ycck32)
        {
            var rgba = InkToRgba32(source);
            if (target == PixelFormat.Rgba32)
            {
                return rgba;
            }

            try
            {
                return Convert(rgba, target, parallel);
            }
            finally
            {
                rgba.Dispose();
            }
        }

        var result = Image.Create(source.Width, source.Height, target);
        try
        {
            var from = source.PixelFormat;
            if (!TryFastPath(source, result, from, target))
            {
                ConvertGeneric(source, result, from, target, parallel);
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static bool TryFastPath(Image source, Image result, PixelFormat from, PixelFormat target)
    {
        var src = source.GetPixelSpan();
        var dst = result.GetPixelSpan();
        int count = source.Width * source.Height;
        switch (from, target)
        {
            case (PixelFormat.Rgb24, PixelFormat.Rgba32):
                PixelFormatConversionKernels.ExpandRgb24ToRgba32(src, dst, count);
                return true;
            case (PixelFormat.Rgba32, PixelFormat.Rgb24):
                PixelFormatConversionKernels.NarrowRgba32ToRgb24(src, dst, count);
                return true;
            case (PixelFormat.Gray8, PixelFormat.Rgb24):
                PixelFormatConversionKernels.ExpandGray8ToRgb24(src, dst, count);
                return true;
            case (PixelFormat.Gray8, PixelFormat.Rgba32):
                PixelFormatConversionKernels.ExpandGray8ToRgba32(src, dst, count);
                return true;
            case (PixelFormat.Rgb24, PixelFormat.Gray8):
                PixelFormatConversionKernels.ComputeLumaFromRgb24(src, dst, count);
                return true;
            case (PixelFormat.Rgba32, PixelFormat.Gray8):
                PixelFormatConversionKernels.ComputeLumaFromRgba32(src, dst, count);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Converts <paramref name="height"/> rows of <paramref name="width"/> pixels of <paramref name="from"/>, <paramref name="sourceStride"/> bytes apart, into a new
    /// image of <paramref name="target"/>, running <paramref name="transform"/> on each row of straight float RGBA in between. The source rows are read in
    /// place (no copy of the source is made), and rows are converted in parallel.
    /// </summary>
    public static Image ConvertRows(ReadOnlyMemory<byte> source, int sourceStride, int width, int height, PixelFormat from, PixelFormat target, ParallelSettings parallel, RowTransform? transform, bool unpremultiply = true)
    {
        var result = Image.Create(width, height, target);
        try
        {
            var resultMemory = result.PixelMemory;
            int resultStride = width * target.GetBytesPerPixel();
            int rowBytes = width * from.GetBytesPerPixel();
            bool sourceGray = from is PixelFormat.Gray8 or PixelFormat.Gray16 or PixelFormat.GrayF32;
            RowParallel.For(height, y =>
            {
                var scratch = ArrayPool<float>.Shared.Rent(width * 4);
                try
                {
                    ConvertOneRow(source.Span.Slice(y * sourceStride, rowBytes), resultMemory.Span.Slice(y * resultStride, resultStride), from, target, width, sourceGray, transform, unpremultiply, scratch);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(scratch);
                }
            }, parallel);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>The same conversion over a span, on the calling thread (a span cannot be captured by parallel work).</summary>
    public static Image ConvertRows(ReadOnlySpan<byte> source, int sourceStride, int width, int height, PixelFormat from, PixelFormat target, RowTransform? transform, bool unpremultiply = true)
    {
        var result = Image.Create(width, height, target);
        var scratch = ArrayPool<float>.Shared.Rent(width * 4);
        try
        {
            var destination = result.GetPixelSpan();
            int resultStride = width * target.GetBytesPerPixel();
            int rowBytes = width * from.GetBytesPerPixel();
            bool sourceGray = from is PixelFormat.Gray8 or PixelFormat.Gray16 or PixelFormat.GrayF32;
            for (int y = 0; y < height; y++)
            {
                ConvertOneRow(source.Slice(y * sourceStride, rowBytes), destination.Slice(y * resultStride, resultStride), from, target, width, sourceGray, transform, unpremultiply, scratch);
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    private static void ConvertOneRow(ReadOnlySpan<byte> sourceRow, Span<byte> targetRow, PixelFormat from, PixelFormat target, int width, bool sourceGray, RowTransform? transform, bool unpremultiply, float[] scratch)
    {
        ReadRow(sourceRow, from, scratch, width, unpremultiply);
        transform?.Invoke(scratch, width);
        WriteRow(scratch, width, sourceGray, target, targetRow);
    }

    private static void ConvertGeneric(Image source, Image result, PixelFormat from, PixelFormat target, ParallelSettings parallel, RowTransform? transform = null, bool unpremultiply = true)
    {
        int width = source.Width;
        var sourceMemory = source.PixelMemory;
        var resultMemory = result.PixelMemory;
        int sourceStride = width * from.GetBytesPerPixel();
        int resultStride = width * target.GetBytesPerPixel();
        bool sourceGray = from is PixelFormat.Gray8 or PixelFormat.Gray16 or PixelFormat.GrayF32;
        RowParallel.For(source.Height, y =>
        {
            var scratch = ArrayPool<float>.Shared.Rent(width * 4);
            try
            {
                ReadRow(sourceMemory.Span.Slice(y * sourceStride, sourceStride), from, scratch, width, unpremultiply);
                transform?.Invoke(scratch, width);
                WriteRow(scratch, width, sourceGray, target, resultMemory.Span.Slice(y * resultStride, resultStride));
            }
            finally
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        }, parallel);
    }

    // Decodes a row to straight float RGBA (gray is replicated, a missing alpha is 1).
    private static void ReadRow(ReadOnlySpan<byte> row, PixelFormat format, float[] rgba, int width, bool unpremultiply = true)
    {
        const float Inv255 = 1f / 255f;
        const float Inv65535 = 1f / 65535f;
        switch (format)
        {
            case PixelFormat.Gray8:
                for (int x = 0; x < width; x++)
                {
                    SetGray(rgba, x, row[x] * Inv255);
                }

                break;
            case PixelFormat.Rgb24:
                for (int x = 0; x < width; x++)
                {
                    Set(rgba, x, row[x * 3] * Inv255, row[(x * 3) + 1] * Inv255, row[(x * 3) + 2] * Inv255, 1f);
                }

                break;
            case PixelFormat.Rgba32:
                RgbaRowKernels.BytesToSingle(row[..(width * 4)], rgba.AsSpan(0, width * 4));
                break;
            case PixelFormat.Gray16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                for (int x = 0; x < width; x++)
                {
                    SetGray(rgba, x, samples[x] * Inv65535);
                }

                break;
            }

            case PixelFormat.Rgb48:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                for (int x = 0; x < width; x++)
                {
                    Set(rgba, x, samples[x * 3] * Inv65535, samples[(x * 3) + 1] * Inv65535, samples[(x * 3) + 2] * Inv65535, 1f);
                }

                break;
            }

            case PixelFormat.Rgba64:
                RgbaRowKernels.UShortsToSingle(MemoryMarshal.Cast<byte, ushort>(row)[..(width * 4)], rgba.AsSpan(0, width * 4));
                break;


            case PixelFormat.GrayF32:
            {
                var samples = MemoryMarshal.Cast<byte, float>(row);
                for (int x = 0; x < width; x++)
                {
                    SetGray(rgba, x, samples[x]);
                }

                break;
            }

            case PixelFormat.RgbF32:
            {
                var samples = MemoryMarshal.Cast<byte, float>(row);
                for (int x = 0; x < width; x++)
                {
                    Set(rgba, x, samples[x * 3], samples[(x * 3) + 1], samples[(x * 3) + 2], 1f);
                }

                break;
            }

            case PixelFormat.RgbaF32:
                MemoryMarshal.Cast<byte, float>(row)[..(width * 4)].CopyTo(rgba);
                break;


            case PixelFormat.RgbaF16:
            case PixelFormat.PremultipliedRgbaF16:
            {
                var samples = MemoryMarshal.Cast<byte, Half>(row);
                RgbaRowKernels.HalfToSingle(samples[..(width * 4)], rgba.AsSpan(0, width * 4));
                if (format == PixelFormat.PremultipliedRgbaF16 && unpremultiply)
                {
                    RgbaRowKernels.Unpremultiply(rgba, width);
                }

                break;
            }

            case PixelFormat.PremultipliedRgba32:
                RgbaRowKernels.BytesToSingle(row[..(width * 4)], rgba.AsSpan(0, width * 4));

                if (unpremultiply)
                {
                    RgbaRowKernels.Unpremultiply(rgba, width);
                }

                break;
            case PixelFormat.PremultipliedRgba64:
            {
                RgbaRowKernels.UShortsToSingle(MemoryMarshal.Cast<byte, ushort>(row)[..(width * 4)], rgba.AsSpan(0, width * 4));
                if (unpremultiply)
                {
                    RgbaRowKernels.Unpremultiply(rgba, width);
                }

                break;
            }

            case PixelFormat.PremultipliedRgbaF32:
            {
                MemoryMarshal.Cast<byte, float>(row)[..(width * 4)].CopyTo(rgba);
                if (unpremultiply)
                {
                    RgbaRowKernels.Unpremultiply(rgba, width);
                }

                break;
            }

            default:
                throw new NotSupportedException($"Converting from {format} is not supported.");
        }
    }

    private static void WriteRow(float[] rgba, int width, bool sourceGray, PixelFormat format, Span<byte> row)
    {
        switch (format)
        {
            case PixelFormat.Gray8:
                for (int x = 0; x < width; x++)
                {
                    row[x] = To8(Gray(rgba, x, sourceGray));
                }

                break;
            case PixelFormat.Rgb24:
                for (int x = 0; x < width; x++)
                {
                    int o = x * 4;
                    row[x * 3] = To8(rgba[o]);
                    row[(x * 3) + 1] = To8(rgba[o + 1]);
                    row[(x * 3) + 2] = To8(rgba[o + 2]);
                }

                break;
            case PixelFormat.Rgba32:
                RgbaRowKernels.SingleToBytes(rgba.AsSpan(0, width * 4), row);
                break;
            case PixelFormat.Gray16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                for (int x = 0; x < width; x++)
                {
                    samples[x] = To16(Gray(rgba, x, sourceGray));
                }

                break;
            }

            case PixelFormat.Rgb48:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                for (int x = 0; x < width; x++)
                {
                    int o = x * 4;
                    samples[x * 3] = To16(rgba[o]);
                    samples[(x * 3) + 1] = To16(rgba[o + 1]);
                    samples[(x * 3) + 2] = To16(rgba[o + 2]);
                }

                break;
            }

            case PixelFormat.Rgba64:
                RgbaRowKernels.SingleToUShorts(rgba.AsSpan(0, width * 4), MemoryMarshal.Cast<byte, ushort>(row));
                break;

            case PixelFormat.GrayF32:
            {
                var samples = MemoryMarshal.Cast<byte, float>(row);
                for (int x = 0; x < width; x++)
                {
                    samples[x] = Gray(rgba, x, sourceGray);
                }

                break;
            }

            case PixelFormat.RgbF32:
            {
                var samples = MemoryMarshal.Cast<byte, float>(row);
                for (int x = 0; x < width; x++)
                {
                    int o = x * 4;
                    samples[x * 3] = rgba[o];
                    samples[(x * 3) + 1] = rgba[o + 1];
                    samples[(x * 3) + 2] = rgba[o + 2];
                }

                break;
            }

            case PixelFormat.RgbaF32:
                rgba.AsSpan(0, width * 4).CopyTo(MemoryMarshal.Cast<byte, float>(row));
                break;
            case PixelFormat.RgbaF16:
                RgbaRowKernels.SingleToHalf(rgba.AsSpan(0, width * 4), MemoryMarshal.Cast<byte, Half>(row));
                break;
            case PixelFormat.PremultipliedRgba32:
            case PixelFormat.PremultipliedRgba64:
            case PixelFormat.PremultipliedRgbaF16:
            case PixelFormat.PremultipliedRgbaF32:
                // Color is multiplied by alpha in float first, then stored exactly as the straight format of the same sample type would be.
                RgbaRowKernels.Premultiply(rgba, width);
                WriteRow(rgba, width, sourceGray, format.ToStraightAlpha(), row);
                break;
            default:
                throw new NotSupportedException($"Converting to {format} is not supported.");
        }
    }

    private static void Set(float[] rgba, int x, float r, float g, float b, float a)
    {
        int o = x * 4;
        rgba[o] = r;
        rgba[o + 1] = g;
        rgba[o + 2] = b;
        rgba[o + 3] = a;
    }

    private static void SetGray(float[] rgba, int x, float value) => Set(rgba, x, value, value, value, 1f);

    // A gray source keeps its value exactly; colour is reduced with BT.601 luma weights on the stored values.
    private static float Gray(float[] rgba, int x, bool sourceGray)
    {
        int o = x * 4;
        return sourceGray ? rgba[o] : (LumaR * rgba[o]) + (LumaG * rgba[o + 1]) + (LumaB * rgba[o + 2]);
    }

    private static byte To8(float value) => value > 0f ? (byte)((Math.Min(value, 1f) * 255f) + 0.5f) : (byte)0;

    private static ushort To16(float value) => value > 0f ? (ushort)((Math.Min(value, 1f) * 65535f) + 0.5f) : (ushort)0;
}
