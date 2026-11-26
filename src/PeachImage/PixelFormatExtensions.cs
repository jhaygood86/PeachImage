namespace PeachImage;

/// <summary>Helpers that describe a <see cref="PixelFormat"/>'s layout.</summary>
public static class PixelFormatExtensions
{
    /// <summary>The size of one pixel in bytes.</summary>
    public static int GetBytesPerPixel(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Rgb24 => 3,
        PixelFormat.Rgba32 => 4,
        PixelFormat.Cmyk32 => 4,
        PixelFormat.Gray16 => 2,
        PixelFormat.Rgb48 => 6,
        PixelFormat.Rgba64 => 8,
        PixelFormat.Ycck32 => 4,
        PixelFormat.GrayF32 => 4,
        PixelFormat.RgbF32 => 12,
        PixelFormat.RgbaF32 => 16,
        PixelFormat.RgbaF16 => 8,
        PixelFormat.PremultipliedRgba32 => 4,
        PixelFormat.PremultipliedRgba64 => 8,
        PixelFormat.PremultipliedRgbaF16 => 8,
        PixelFormat.PremultipliedRgbaF32 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, message: null),
    };

    /// <summary>The number of channels per pixel.</summary>
    public static int GetChannelCount(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Rgb24 => 3,
        PixelFormat.Rgba32 => 4,
        PixelFormat.Cmyk32 => 4,
        PixelFormat.Gray16 => 1,
        PixelFormat.Rgb48 => 3,
        PixelFormat.Rgba64 => 4,
        PixelFormat.Ycck32 => 4,
        PixelFormat.GrayF32 => 1,
        PixelFormat.RgbF32 => 3,
        PixelFormat.RgbaF32 => 4,
        PixelFormat.RgbaF16 => 4,
        PixelFormat.PremultipliedRgba32 => 4,
        PixelFormat.PremultipliedRgba64 => 4,
        PixelFormat.PremultipliedRgbaF16 => 4,
        PixelFormat.PremultipliedRgbaF32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, message: null),
    };

    /// <summary>The size of one channel sample in bytes (1, 2 or 4).</summary>
    public static int GetBytesPerSample(this PixelFormat format) => format switch
    {
        PixelFormat.GrayF32 or PixelFormat.RgbF32 or PixelFormat.RgbaF32 or PixelFormat.PremultipliedRgbaF32 => 4,
        PixelFormat.Gray16 or PixelFormat.Rgb48 or PixelFormat.Rgba64 or PixelFormat.RgbaF16 or PixelFormat.PremultipliedRgba64 or PixelFormat.PremultipliedRgbaF16 => 2,
        _ => 1,
    };

    /// <summary>Whether samples are floating point (32-bit or half precision), so values outside 0..1 are representable.</summary>
    public static bool IsFloat(this PixelFormat format) =>
        format is PixelFormat.GrayF32 or PixelFormat.RgbF32 or PixelFormat.RgbaF32 or PixelFormat.RgbaF16 or PixelFormat.PremultipliedRgbaF16 or PixelFormat.PremultipliedRgbaF32;

    /// <summary>Whether samples are IEEE 754 half-precision floats.</summary>
    public static bool IsHalfFloat(this PixelFormat format) => format is PixelFormat.RgbaF16 or PixelFormat.PremultipliedRgbaF16;

    /// <summary>Whether the format has an alpha channel.</summary>
    public static bool HasAlpha(this PixelFormat format) =>
        format is PixelFormat.Rgba32 or PixelFormat.Rgba64 or PixelFormat.RgbaF32 or PixelFormat.RgbaF16
            or PixelFormat.PremultipliedRgba32 or PixelFormat.PremultipliedRgba64 or PixelFormat.PremultipliedRgbaF16 or PixelFormat.PremultipliedRgbaF32;

    /// <summary>Whether color is stored multiplied by alpha.</summary>
    public static bool IsPremultiplied(this PixelFormat format) =>
        format is PixelFormat.PremultipliedRgba32 or PixelFormat.PremultipliedRgba64 or PixelFormat.PremultipliedRgbaF16 or PixelFormat.PremultipliedRgbaF32;

    /// <summary>The straight-alpha format with the same sample type (the format itself when it is not premultiplied).</summary>
    public static PixelFormat ToStraightAlpha(this PixelFormat format) => format switch
    {
        PixelFormat.PremultipliedRgba32 => PixelFormat.Rgba32,
        PixelFormat.PremultipliedRgba64 => PixelFormat.Rgba64,
        PixelFormat.PremultipliedRgbaF16 => PixelFormat.RgbaF16,
        PixelFormat.PremultipliedRgbaF32 => PixelFormat.RgbaF32,
        _ => format,
    };

    /// <summary>The premultiplied-alpha format with the same sample type, or <paramref name="format"/> itself when it has no premultiplied counterpart (no alpha, or already premultiplied).</summary>
    public static PixelFormat ToPremultipliedAlpha(this PixelFormat format) => format switch
    {
        PixelFormat.Rgba32 => PixelFormat.PremultipliedRgba32,
        PixelFormat.Rgba64 => PixelFormat.PremultipliedRgba64,
        PixelFormat.RgbaF16 => PixelFormat.PremultipliedRgbaF16,
        PixelFormat.RgbaF32 => PixelFormat.PremultipliedRgbaF32,
        _ => format,
    };
}
