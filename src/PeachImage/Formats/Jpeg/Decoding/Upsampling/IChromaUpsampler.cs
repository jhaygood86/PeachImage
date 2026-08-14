namespace PeachImage.Formats.Jpeg.Decoding.Upsampling;

/// <summary>Upsamples a subsampled chroma (or any non-full-resolution) plane to the frame's full sample grid.</summary>
internal interface IChromaUpsampler
{
    /// <summary>
    /// Upsamples <paramref name="source"/> (<paramref name="sourceWidth"/> x <paramref name="sourceHeight"/>) by
    /// the given integer ratios into <paramref name="destination"/> (<paramref name="destinationWidth"/> x
    /// <paramref name="destinationHeight"/>), which must be at least <c>sourceWidth * horizontalRatio</c> wide
    /// and <c>sourceHeight * verticalRatio</c> tall.
    /// </summary>
    void Upsample(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destinationWidth, int destinationHeight,
        int horizontalRatio, int verticalRatio);
}
