namespace PeachImage.Formats.Webp.Kernels;

/// <summary>
/// Hardware-tier-dispatched kernels for VP8L transform-inverse operations that have no cross-pixel
/// dependency (so are safe to vectorize) — selected once at startup by <see cref="Vp8LTransformKernelSelector"/>,
/// mirroring <c>Jpeg.ColorConversion.ColorConverterSelector</c>'s Vector256 &gt; Vector128 &gt; scalar dispatch.
/// The color transform's inverse is deliberately *not* here: it has a genuine same-pixel cross-channel
/// dependency (the blue channel's second delta reads the just-computed, masked red channel) that a
/// correctness-first first pass left scalar-only rather than risk a subtly wrong vectorization — see
/// <c>Vp8LColorTransform</c>'s remarks.
/// </summary>
internal interface IVp8LTransformKernel
{
    /// <summary>In place, per pixel: <c>red += green; blue += green;</c> (mod 256, no clamp) — the subtract-green transform's inverse.</summary>
    void SubtractGreenInverse(Span<uint> pixels);

    /// <summary>
    /// In place: <c>row[i] += topRow[i]</c> for every raw ARGB byte in the row (mod 256, no clamp) — the
    /// predictor transform's mode 2 ("top") inverse, applied to a whole row at once. Byte-lane wraparound
    /// addition is channel-safe on its own (each byte lane carries/overflows independently in hardware), so
    /// this is exactly <c>Png.Filtering.VectorizedRowFilter.UnfilterUp</c>'s operation, just re-expressed
    /// with this type's Vector256/128/scalar tiers instead of <c>System.Numerics.Vector&lt;T&gt;</c>'s
    /// single portable width.
    /// </summary>
    void PredictorTopInverse(Span<byte> row, ReadOnlySpan<byte> topRow);
}
