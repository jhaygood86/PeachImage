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
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, message: null),
    };

    /// <summary>Gets the number of color/alpha channels for the given <paramref name="format"/>.</summary>
    public static int GetChannelCount(this PixelFormat format) => GetBytesPerPixel(format);
}
