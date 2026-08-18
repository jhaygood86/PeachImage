namespace PeachImage.Formats.Webp.Kernels;

/// <summary>
/// Hardware-tier-dispatched kernels for VP8L transform-inverse operations that have no cross-pixel
/// dependency (so are safe to vectorize) — selected once at startup by <see cref="Vp8LTransformKernelSelector"/>,
/// mirroring <c>Jpeg.ColorConversion.ColorConverterSelector</c>'s Vector256 &gt; Vector128 &gt; scalar dispatch.
/// The color transform's "same-pixel dependency" (the blue channel's second delta reads the just-computed,
/// masked red channel) is within one pixel's 3-step chain, not across pixels — every pixel in a tile run
/// uses the same 3 constant multipliers and only reads/writes its own ARGB word, so it is embarrassingly
/// parallel across pixels/lanes the same way the other two members here are, unlike the predictor
/// transform's non-"top" modes or PNG's row filters, which read the *neighboring* pixel's already-
/// reconstructed value.
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

    /// <summary>
    /// In place, per pixel: predicts red from green and blue from (green, the just-predicted red) via the
    /// cross-color transform's inverse — see <c>Vp8LColorTransform</c>'s remarks for the exact per-pixel
    /// formula. <paramref name="greenToRed"/>/<paramref name="greenToBlue"/>/<paramref name="redToBlue"/> are
    /// constant for the whole span (one tile's multipliers).
    /// </summary>
    void ColorTransformInverse(Span<uint> pixels, sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue);
}
