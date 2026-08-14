namespace PeachImage.Formats.Jpeg.ColorConversion;

/// <summary>Converts between JPEG's native luma/chroma sample representations and RGB/CMYK output.</summary>
internal interface IColorConverter
{
    /// <summary>Converts <paramref name="pixelCount"/> full-resolution Y/Cb/Cr samples to tightly-packed RGB triples.</summary>
    void YCbCrToRgb(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<byte> rgb, int pixelCount);

    /// <summary>Converts <paramref name="pixelCount"/> full-resolution Y/Cb/Cr/K samples to tightly-packed CMYK quadruples.</summary>
    void YcckToCmyk(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, ReadOnlySpan<byte> k, Span<byte> cmyk, int pixelCount);

    /// <summary>Converts <paramref name="pixelCount"/> full-resolution R/G/B samples (the level-shifted-back FDCT input) back to tightly-packed Y/Cb/Cr for encoding.</summary>
    void RgbToYCbCr(ReadOnlySpan<byte> rgb, Span<byte> y, Span<byte> cb, Span<byte> cr, int pixelCount);
}
