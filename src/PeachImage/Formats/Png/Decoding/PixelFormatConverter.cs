using System.Runtime.InteropServices;
using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Formats.Png.Decoding;

/// <summary>Converts a decoded <see cref="Image"/> to the pixel format requested via <see cref="DecoderOptions.TargetPixelFormat"/>, scoped to the formats PngDecoder actually produces plus their 8/16-bit width conversions. The 8-bit, generic bit-width, and 16-bit-source-to-Rgba32 cases delegate to the shared, SIMD-optimized <see cref="PixelFormatConversionKernels"/>; the remaining channel-count-changing 16-bit-to-16-bit conversions (Rgb48/Rgba64/Gray16 interconversion) are PNG-only, low-traffic, and stay as the original scalar per-pixel loop.</summary>
internal static class PixelFormatConverter
{
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

            (PixelFormat.Gray8, PixelFormat.Gray16) => ConvertWiden(image, PixelFormat.Gray16, PixelFormatConversionKernels.WidenBytesToUInt16),
            (PixelFormat.Rgb24, PixelFormat.Rgb48) => ConvertWiden(image, PixelFormat.Rgb48, PixelFormatConversionKernels.WidenBytesToUInt16),
            (PixelFormat.Rgba32, PixelFormat.Rgba64) => ConvertWiden(image, PixelFormat.Rgba64, PixelFormatConversionKernels.WidenBytesToUInt16),

            (PixelFormat.Gray16, PixelFormat.Gray8) => ConvertNarrow(image, PixelFormat.Gray8, PixelFormatConversionKernels.NarrowUInt16ToBytes),
            (PixelFormat.Rgb48, PixelFormat.Rgb24) => ConvertNarrow(image, PixelFormat.Rgb24, PixelFormatConversionKernels.NarrowUInt16ToBytes),
            (PixelFormat.Rgba64, PixelFormat.Rgba32) => ConvertNarrow(image, PixelFormat.Rgba32, PixelFormatConversionKernels.NarrowUInt16ToBytes),

            (PixelFormat.Rgb48, PixelFormat.Rgba64) => Expand16(image, 4, static (s, d) => { s[..3].CopyTo(d); d[3] = 65535; }),
            (PixelFormat.Rgba64, PixelFormat.Rgb48) => Expand16(image, 3, static (s, d) => s[..3].CopyTo(d)),
            (PixelFormat.Gray16, PixelFormat.Rgb48) => Expand16(image, 3, static (s, d) => d[0] = d[1] = d[2] = s[0]),
            (PixelFormat.Gray16, PixelFormat.Rgba64) => Expand16(image, 4, static (s, d) => { d[0] = d[1] = d[2] = s[0]; d[3] = 65535; }),

            // Direct 16-bit-source -> Rgba32 single-hop narrowing (top-byte truncation), so callers requesting
            // Rgba32 don't need to chain through Rgba64/Rgb48 themselves.
            (PixelFormat.Gray16, PixelFormat.Rgba32) => ConvertReshape16(image, PixelFormat.Rgba32, PixelFormatConversionKernels.ExpandNarrowGray16ToRgba32),
            (PixelFormat.Rgb48, PixelFormat.Rgba32) => ConvertReshape16(image, PixelFormat.Rgba32, PixelFormatConversionKernels.ExpandNarrowRgb48ToRgba32),

            _ => throw new PngDecodingException($"Cannot convert decoded {image.PixelFormat} pixels to requested format {targetFormat}."),
        };

        image.Dispose();
        return converted;
    }

    private delegate void Reshape8(ReadOnlySpan<byte> source, Span<byte> destination, int pixelCount);

    private delegate void Widen8To16(ReadOnlySpan<byte> source, Span<ushort> destination);

    private delegate void Narrow16To8(ReadOnlySpan<ushort> source, Span<byte> destination);

    private delegate void Reshape16To8(ReadOnlySpan<ushort> source, Span<byte> destination, int pixelCount);

    private static Image Convert(Image source, PixelFormat destFormat, Reshape8 kernel)
    {
        var dest = Image.Create(source.Width, source.Height, destFormat);
        kernel(source.GetPixelSpan(), dest.GetPixelSpan(), source.Width * source.Height);
        return dest;
    }

    private static Image ConvertWiden(Image source, PixelFormat destFormat, Widen8To16 kernel)
    {
        var dest = Image.Create(source.Width, source.Height, destFormat);
        kernel(source.GetPixelSpan(), MemoryMarshal.Cast<byte, ushort>(dest.GetPixelSpan()));
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

    private static Image Expand16(Image source, int destChannels, PixelConversion16 convert)
    {
        int srcChannels = source.PixelFormat.GetChannelCount();
        var destFormat = destChannels switch
        {
            1 => PixelFormat.Gray16,
            3 => PixelFormat.Rgb48,
            4 => PixelFormat.Rgba64,
            _ => throw new ArgumentOutOfRangeException(nameof(destChannels)),
        };

        var dest = Image.Create(source.Width, source.Height, destFormat);
        var src = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
        var dst = MemoryMarshal.Cast<byte, ushort>(dest.GetPixelSpan());
        int pixelCount = source.Width * source.Height;

        for (int i = 0; i < pixelCount; i++)
        {
            convert(src.Slice(i * srcChannels, srcChannels), dst.Slice(i * destChannels, destChannels));
        }

        return dest;
    }

    private delegate void PixelConversion16(ReadOnlySpan<ushort> source, Span<ushort> destination);
}
