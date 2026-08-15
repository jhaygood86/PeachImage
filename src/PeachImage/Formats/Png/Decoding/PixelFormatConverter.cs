using System.Runtime.InteropServices;

namespace PeachImage.Formats.Png.Decoding;

/// <summary>Converts a decoded <see cref="Image"/> to the pixel format requested via <see cref="DecoderOptions.TargetPixelFormat"/>. PNG's own copy — mirrors Bmp/Jpeg's converter, scoped to the formats PngDecoder actually produces plus their 8/16-bit width conversions.</summary>
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
            (PixelFormat.Rgb24, PixelFormat.Rgba32) => Expand8(image, 3, static (s, d) => { s[..3].CopyTo(d); d[3] = 255; }),
            (PixelFormat.Rgba32, PixelFormat.Rgb24) => Expand8(image, 3, static (s, d) => s[..3].CopyTo(d)),
            (PixelFormat.Rgb24, PixelFormat.Gray8) => Expand8(image, 1, static (s, d) => d[0] = Luma(s[0], s[1], s[2])),
            (PixelFormat.Rgba32, PixelFormat.Gray8) => Expand8(image, 1, static (s, d) => d[0] = Luma(s[0], s[1], s[2])),
            (PixelFormat.Gray8, PixelFormat.Rgb24) => Expand8(image, 3, static (s, d) => d[0] = d[1] = d[2] = s[0]),
            (PixelFormat.Gray8, PixelFormat.Rgba32) => Expand8(image, 4, static (s, d) => { d[0] = d[1] = d[2] = s[0]; d[3] = 255; }),

            (PixelFormat.Gray8, PixelFormat.Gray16) => Widen(image, 1, PixelFormat.Gray16),
            (PixelFormat.Rgb24, PixelFormat.Rgb48) => Widen(image, 3, PixelFormat.Rgb48),
            (PixelFormat.Rgba32, PixelFormat.Rgba64) => Widen(image, 4, PixelFormat.Rgba64),

            (PixelFormat.Gray16, PixelFormat.Gray8) => Narrow(image, 1, PixelFormat.Gray8),
            (PixelFormat.Rgb48, PixelFormat.Rgb24) => Narrow(image, 3, PixelFormat.Rgb24),
            (PixelFormat.Rgba64, PixelFormat.Rgba32) => Narrow(image, 4, PixelFormat.Rgba32),

            (PixelFormat.Rgb48, PixelFormat.Rgba64) => Expand16(image, 4, static (s, d) => { s[..3].CopyTo(d); d[3] = 65535; }),
            (PixelFormat.Rgba64, PixelFormat.Rgb48) => Expand16(image, 3, static (s, d) => s[..3].CopyTo(d)),
            (PixelFormat.Gray16, PixelFormat.Rgb48) => Expand16(image, 3, static (s, d) => d[0] = d[1] = d[2] = s[0]),
            (PixelFormat.Gray16, PixelFormat.Rgba64) => Expand16(image, 4, static (s, d) => { d[0] = d[1] = d[2] = s[0]; d[3] = 65535; }),

            _ => throw new PngDecodingException($"Cannot convert decoded {image.PixelFormat} pixels to requested format {targetFormat}."),
        };

        image.Dispose();
        return converted;
    }

    private static byte Luma(byte r, byte g, byte b) =>
        (byte)Math.Clamp(Math.Round((0.299 * r) + (0.587 * g) + (0.114 * b), MidpointRounding.AwayFromZero), 0, 255);

    private static Image Expand8(Image source, int destBytesPerPixel, PixelConversion8 convert)
    {
        int srcBpp = source.PixelFormat.GetBytesPerPixel();
        var destFormat = destBytesPerPixel switch
        {
            1 => PixelFormat.Gray8,
            3 => PixelFormat.Rgb24,
            4 => PixelFormat.Rgba32,
            _ => throw new ArgumentOutOfRangeException(nameof(destBytesPerPixel)),
        };

        var dest = Image.Create(source.Width, source.Height, destFormat);
        var src = source.GetPixelSpan();
        var dst = dest.GetPixelSpan();
        int pixelCount = source.Width * source.Height;

        for (int i = 0; i < pixelCount; i++)
        {
            convert(src.Slice(i * srcBpp, srcBpp), dst.Slice(i * destBytesPerPixel, destBytesPerPixel));
        }

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

    private static Image Widen(Image source, int channels, PixelFormat destFormat)
    {
        var dest = Image.Create(source.Width, source.Height, destFormat);
        var src = source.GetPixelSpan();
        var dst = MemoryMarshal.Cast<byte, ushort>(dest.GetPixelSpan());

        for (int i = 0; i < src.Length; i++)
        {
            byte v = src[i];
            dst[i] = (ushort)((v << 8) | v);
        }

        return dest;
    }

    private static Image Narrow(Image source, int channels, PixelFormat destFormat)
    {
        var dest = Image.Create(source.Width, source.Height, destFormat);
        var src = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
        var dst = dest.GetPixelSpan();

        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = (byte)(src[i] >> 8);
        }

        return dest;
    }

    private delegate void PixelConversion8(ReadOnlySpan<byte> source, Span<byte> destination);

    private delegate void PixelConversion16(ReadOnlySpan<ushort> source, Span<ushort> destination);
}
