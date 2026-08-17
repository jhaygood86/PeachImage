using System.Runtime.InteropServices;
using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Formats.Avif.Decoding;

/// <summary>Converts a decoded <see cref="Image"/> to the pixel format requested via <see cref="DecoderOptions.TargetPixelFormat"/>, scoped to the formats <see cref="AvifDecoder"/> actually produces: Rgb24/Gray8 for 8-bit sources, plus Rgb48/Gray16/Rgba64 for &gt;8-bit sources (see <c>AvifPixelFormatSelector</c>). The actual pixel reshaping is done by the shared, SIMD-optimized <see cref="PixelFormatConversionKernels"/> — this class only owns the supported-pair dispatch and AVIF's own exception type.</summary>
internal static class PixelFormatConverter
{
    /// <summary>Converts <paramref name="image"/> to <paramref name="target"/> if needed, disposing the original when a new image is produced.</summary>
    public static Image ConvertIfNeeded(Image image, PixelFormat? target)
    {
        if (target is not { } targetFormat || targetFormat == image.PixelFormat)
        {
            return image;
        }

        var converted = (image.PixelFormat, targetFormat) switch
        {
            (PixelFormat.Rgb24, PixelFormat.Rgba32) => Convert(image, PixelFormat.Rgba32, PixelFormatConversionKernels.ExpandRgb24ToRgba32),
            (PixelFormat.Rgba32, PixelFormat.Rgb24) => Convert(image, PixelFormat.Rgb24, PixelFormatConversionKernels.NarrowRgba32ToRgb24),
            (PixelFormat.Rgb24, PixelFormat.Gray8) => Convert(image, PixelFormat.Gray8, PixelFormatConversionKernels.ComputeLumaFromRgb24),
            (PixelFormat.Rgba32, PixelFormat.Gray8) => Convert(image, PixelFormat.Gray8, PixelFormatConversionKernels.ComputeLumaFromRgba32),
            (PixelFormat.Gray8, PixelFormat.Rgb24) => Convert(image, PixelFormat.Rgb24, PixelFormatConversionKernels.ExpandGray8ToRgb24),
            (PixelFormat.Gray8, PixelFormat.Rgba32) => Convert(image, PixelFormat.Rgba32, PixelFormatConversionKernels.ExpandGray8ToRgba32),

            // Direct 16-bit-source (10/12-bit AVIF) -> Rgba32 single-hop narrowing (top-byte truncation), so
            // callers requesting Rgba32 don't need to widen/narrow through Rgb48/Rgba64 themselves.
            (PixelFormat.Gray16, PixelFormat.Rgba32) => ConvertReshape16(image, PixelFormat.Rgba32, PixelFormatConversionKernels.ExpandNarrowGray16ToRgba32),
            (PixelFormat.Rgb48, PixelFormat.Rgba32) => ConvertReshape16(image, PixelFormat.Rgba32, PixelFormatConversionKernels.ExpandNarrowRgb48ToRgba32),
            (PixelFormat.Rgba64, PixelFormat.Rgba32) => ConvertNarrow(image, PixelFormat.Rgba32, PixelFormatConversionKernels.NarrowUInt16ToBytes),

            _ => throw new AvifDecodingException($"Cannot convert decoded {image.PixelFormat} pixels to requested format {targetFormat}."),
        };

        image.Dispose();
        return converted;
    }

    private delegate void Reshape8(ReadOnlySpan<byte> source, Span<byte> destination, int pixelCount);

    private delegate void Narrow16To8(ReadOnlySpan<ushort> source, Span<byte> destination);

    private delegate void Reshape16To8(ReadOnlySpan<ushort> source, Span<byte> destination, int pixelCount);

    private static Image Convert(Image source, PixelFormat destFormat, Reshape8 kernel)
    {
        var dest = Image.Create(source.Width, source.Height, destFormat);
        kernel(source.GetPixelSpan(), dest.GetPixelSpan(), source.Width * source.Height);
        return dest;
    }

    private static Image ConvertNarrow(Image source, PixelFormat destFormat, Narrow16To8 kernel)
    {
        var dest = Image.Create(source.Width, source.Height, destFormat);
        kernel(MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan()), dest.GetPixelSpan());
        return dest;
    }

    private static Image ConvertReshape16(Image source, PixelFormat destFormat, Reshape16To8 kernel)
    {
        var dest = Image.Create(source.Width, source.Height, destFormat);
        kernel(MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan()), dest.GetPixelSpan(), source.Width * source.Height);
        return dest;
    }
}
