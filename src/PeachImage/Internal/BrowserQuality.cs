namespace PeachImage.Internal;

/// <summary>Maps the 0..1 <c>quality</c> argument of a canvas <c>toBlob(type, quality)</c> onto a codec's 0..100 scale the way browsers do.</summary>
internal static class BrowserQuality
{
    /// <summary>
    /// <c>round(quality x 100)</c>. A browser ignores a quality outside 0..1 and uses its default, which is the caller's choice here (it knows its
    /// defaults), so out-of-range and NaN values are an error rather than silently replaced.
    /// </summary>
    public static int ToPercent(float quality)
    {
        if (!(quality >= 0f && quality <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(quality), quality, "Quality must be between 0 and 1.");
        }

        return (int)MathF.Round(quality * 100f, MidpointRounding.AwayFromZero);
    }
}
