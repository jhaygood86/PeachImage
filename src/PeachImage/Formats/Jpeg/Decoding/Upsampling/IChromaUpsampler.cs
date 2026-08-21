namespace PeachImage.Formats.Jpeg.Decoding.Upsampling;

/// <summary>Upsamples a subsampled chroma (or any non-full-resolution) plane to the frame's full sample grid.</summary>
internal interface IChromaUpsampler
{
    /// <summary>
    /// Upsamples <paramref name="source"/> (<paramref name="sourceWidth"/> x <paramref name="sourceHeight"/>) by
    /// the given integer ratios into <paramref name="destination"/> (<paramref name="destinationWidth"/> x
    /// <paramref name="destinationHeight"/>), which must be at least <c>sourceWidth * horizontalRatio</c> wide
    /// and <c>sourceHeight * verticalRatio</c> tall. Plain arrays, not <see cref="Span{T}"/>/
    /// <see cref="ReadOnlySpan{T}"/> — same reason as <c>IResamplingConvolver</c>: implementations
    /// parallelize their per-row loop, and a <see cref="Span{T}"/> is a ref struct that can't be captured by
    /// the closure that requires. A caller-owned array may be larger than the logical region a call
    /// reads/writes (e.g. an <see cref="System.Buffers.ArrayPool{T}"/> rental); every index touched is
    /// bounded by this call's own width/height parameters, never by either array's <c>Length</c>.
    /// </summary>
    void Upsample(
        byte[] source, int sourceWidth, int sourceHeight,
        byte[] destination, int destinationWidth, int destinationHeight,
        int horizontalRatio, int verticalRatio);
}
