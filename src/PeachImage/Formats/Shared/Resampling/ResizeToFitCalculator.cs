namespace PeachImage.Formats.Shared.Resampling;

/// <summary>
/// Computes the largest size that fits within a bounding box while preserving aspect ratio, without ever
/// upscaling — shared by <see cref="Image.Resize(int, int, ResizeOptions?)"/> and
/// <see cref="AnimatedImage.Resize(int, int, ResizeOptions?)"/> so both apply the identical shrink-only,
/// aspect-preserving math when <see cref="ResizeOptions.Mode"/> is <see cref="ResizeMode.Max"/>.
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

    /// <summary>
    /// Scales so the source fills (covers) <paramref name="targetWidth"/> x <paramref name="targetHeight"/>
    /// while preserving aspect ratio — the larger of the two axis scales, so the result is always at least
    /// as large as the target on both axes (exactly equal on the constraining axis). Used by
    /// <see cref="ResizeMode.Crop"/>, where the overflow on the non-constraining axis is cropped away.
    /// </summary>
    public static (int Width, int Height) ComputeFillDimensions(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        double scale = Math.Max((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
        int width = Math.Max(targetWidth, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(targetHeight, (int)Math.Round(sourceHeight * scale));
        return (width, height);
    }

    /// <summary>
    /// Scales so the source fits within <paramref name="targetWidth"/> x <paramref name="targetHeight"/>
    /// while preserving aspect ratio — like <see cref="ComputeFitDimensions"/>, but always applies the scale
    /// rather than only shrinking, so this can upscale a source smaller than the target. Used by
    /// <see cref="ResizeMode.Pad"/>, where the remainder on the non-constraining axis is padded.
    /// </summary>
    public static (int Width, int Height) ComputeUnrestrictedFitDimensions(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        double scale = Math.Min((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
        int width = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, targetWidth);
        int height = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, targetHeight);
        return (width, height);
    }
}
