namespace PeachImage.Formats.Shared.Resampling;

/// <summary>
/// Computes the largest size that fits within a bounding box while preserving aspect ratio, without ever
/// upscaling — shared by <see cref="Image.ResizeToFit(int, int, ResizeOptions?)"/> and
/// <see cref="AnimatedImage.ResizeToFit(int, int, ResizeOptions?)"/> so both apply the identical
/// shrink-only, aspect-preserving math.
/// </summary>
internal static class ResizeToFitCalculator
{
    /// <summary>
    /// Returns <paramref name="sourceWidth"/>/<paramref name="sourceHeight"/> unchanged if the source
    /// already fits within <paramref name="maxWidth"/> x <paramref name="maxHeight"/>; otherwise scales both
    /// down by whichever axis is more constrained, rounding to the nearest pixel but never below 1.
    /// </summary>
    public static (int Width, int Height) ComputeFitDimensions(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        if (sourceWidth <= maxWidth && sourceHeight <= maxHeight)
        {
            return (sourceWidth, sourceHeight);
        }

        double scale = Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight);
        int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return (width, height);
    }
}
