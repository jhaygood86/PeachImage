namespace PeachImage;

/// <summary>Helper methods for reasoning about <see cref="PixelFormat"/> values.</summary>
public static class PixelFormatExtensions
{
    /// <summary>Gets the number of bytes a single pixel occupies for the given <paramref name="format"/>.</summary>
    public static int GetBytesPerPixel(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Rgb24 => 3,
        PixelFormat.Rgba32 => 4,
        PixelFormat.Cmyk32 => 4,
        PixelFormat.Gray16 => 2,
        PixelFormat.Rgb48 => 6,
        PixelFormat.Rgba64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, message: null),
    };

    /// <summary>Gets the number of color/alpha channels for the given <paramref name="format"/>.</summary>
    public static int GetChannelCount(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Rgb24 => 3,
        PixelFormat.Rgba32 => 4,
        PixelFormat.Cmyk32 => 4,
        PixelFormat.Gray16 => 1,
        PixelFormat.Rgb48 => 3,
        PixelFormat.Rgba64 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, message: null),
    };

    /// <summary>Gets the number of bytes a single sample (channel value) occupies for the given <paramref name="format"/>.</summary>
    public static int GetBytesPerSample(this PixelFormat format) => format switch
    {
        PixelFormat.Gray16 or PixelFormat.Rgb48 or PixelFormat.Rgba64 => 2,
        _ => 1,
    };
}
