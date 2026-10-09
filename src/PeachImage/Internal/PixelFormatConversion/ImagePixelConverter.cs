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

    public static Image Convert(Image source, PixelFormat target)
    {
        var result = Image.Create(source.Width, source.Height, target);
        try
        {
            var from = source.PixelFormat;
            if (!TryFastPath(source, result, from, target))
            {
                ConvertGeneric(source, result, from, target);
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

    private static void ConvertGeneric(Image source, Image result, PixelFormat from, PixelFormat target)
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
                ReadRow(sourceMemory.Span.Slice(y * sourceStride, sourceStride), from, scratch, width);
                WriteRow(scratch, width, sourceGray, target, resultMemory.Span.Slice(y * resultStride, resultStride));
            }
            finally
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        });
    }

    // Decodes a row to straight float RGBA (gray is replicated, a missing alpha is 1).
    private static void ReadRow(ReadOnlySpan<byte> row, PixelFormat format, float[] rgba, int width)
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
                for (int x = 0; x < width; x++)
                {
                    int o = x * 4;
                    Set(rgba, x, row[o] * Inv255, row[o + 1] * Inv255, row[o + 2] * Inv255, row[o + 3] * Inv255);
                }

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
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                for (int x = 0; x < width; x++)
                {
                    int o = x * 4;
                    Set(rgba, x, samples[o] * Inv65535, samples[o + 1] * Inv65535, samples[o + 2] * Inv65535, samples[o + 3] * Inv65535);
                }

                break;
            }

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
            {
                var samples = MemoryMarshal.Cast<byte, float>(row);
                for (int x = 0; x < width; x++)
                {
                    int o = x * 4;
                    Set(rgba, x, samples[o], samples[o + 1], samples[o + 2], samples[o + 3]);
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
                for (int x = 0; x < width * 4; x++)
                {
                    row[x] = To8(rgba[x]);
                }

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
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(row);
                for (int x = 0; x < width * 4; x++)
                {
                    samples[x] = To16(rgba[x]);
                }

                break;
            }

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
