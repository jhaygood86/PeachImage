namespace PeachImage.Formats.Webp.Kernels;

/// <summary>
/// Hardware-tier-dispatched kernels for the two pixel-repacking steps <c>Encoding.WebpFrameEncoder</c> runs
/// on every <see cref="PixelFormat.Rgba32"/> source -- once per still image, or once per animation frame for
/// <c>Encoding.WebpAnimationEncoder</c> -- selected once at startup by <see cref="WebpPixelPackKernelSelector"/>,
/// mirroring <see cref="Vp8LTransformKernelSelector"/>'s Vector256 &gt; Vector128 &gt; scalar dispatch. Both
/// operations are a fixed per-pixel byte permutation with no cross-pixel dependency, so are safe to
/// vectorize freely.
/// </summary>
internal interface IWebpPixelPackKernel
{
    /// <summary>
    /// Packs <paramref name="rgba"/> (raw R,G,B,A byte quadruples) into <paramref name="argb"/> as
    /// <c>0xAARRGGBB</c> values -- a per-pixel swap of the R and B bytes. Returns whether the source needs a
    /// real alpha channel: <see langword="false"/> when every pixel's alpha byte is <c>0xFF</c> (uniformly
    /// opaque), matching cwebp's own default of skipping a trivial alpha plane.
    /// </summary>
    bool GatherRgba32(ReadOnlySpan<byte> rgba, Span<uint> argb);

    /// <summary>
    /// Drops the alpha byte from each <c>0xAARRGGBB</c> value in <paramref name="argb"/> into a fresh R,G,B
    /// byte triple in <paramref name="rgb"/>, for <c>Encoding.Vp8.Vp8ImageEncoder</c>, which has no alpha
    /// plane to feed.
    /// </summary>
    void ExtractRgb(ReadOnlySpan<uint> argb, Span<byte> rgb);
}
