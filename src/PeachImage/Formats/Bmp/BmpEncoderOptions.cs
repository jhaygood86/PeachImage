namespace PeachImage.Formats.Bmp;

/// <summary>BMP-specific encode options.</summary>
public sealed class BmpEncoderOptions : EncoderOptions
{
    /// <summary>
    /// Compresses 8bpp indexed (<see cref="PixelFormat.Gray8"/>) output with RLE8. Ignored for
    /// <see cref="PixelFormat.Rgb24"/>/<see cref="PixelFormat.Rgba32"/> sources, which BMP cannot RLE-compress.
    /// </summary>
    public bool UseRunLengthEncoding { get; init; }
}
