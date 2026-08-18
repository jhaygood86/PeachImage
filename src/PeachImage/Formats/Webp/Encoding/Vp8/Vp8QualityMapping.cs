namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// Maps <see cref="WebpEncoderOptions.Quality"/> (0-100, JPEG-style, higher = better/larger) onto VP8's own
/// quantizer index (0-127, <em>lower</em> = better/less quantization — the inverted sense from JPEG's own
/// quality scale) and loop filter strength.
/// </summary>
/// <remarks>
/// Both mappings here are deliberately linear v1 placeholders, not the non-linear rate/distortion-tuned curves
/// libwebp's own <c>cwebp -q</c> uses — analogous to how <see cref="Jpeg.Encoding.QuantizationTableFactory"/>'s
/// quality curve was itself tuned against measured output rather than assumed. Recalibrating these against
/// corpus-measured bits-per-pixel/PSNR curves is a real quality/efficiency improvement left for a later
/// milestone; a linear mapping is not wrong, just unrefined — every quality level still produces a valid,
/// correctly-decodable bitstream, and quality still monotonically trades off size against fidelity.
/// </remarks>
internal static class Vp8QualityMapping
{
    /// <summary>Maps <paramref name="quality"/> (0-100) to VP8's base quantizer index (0-127).</summary>
    public static int QualityToBaseQIndex(int quality)
    {
        int clampedQuality = Math.Clamp(quality, 0, 100);
        int qIndex = 127 - (int)Math.Round(clampedQuality * 127 / 100.0);
        return Math.Clamp(qIndex, 0, 127);
    }

    /// <summary>Maps <paramref name="quality"/> (0-100) to a loop filter level (0-63) -- higher quality (less quantization noise) needs less deblocking.</summary>
    public static int QualityToFilterLevel(int quality)
    {
        int clampedQuality = Math.Clamp(quality, 0, 100);
        int level = (int)Math.Round((100 - clampedQuality) * 0.5);
        return Math.Clamp(level, 0, 63);
    }
}
