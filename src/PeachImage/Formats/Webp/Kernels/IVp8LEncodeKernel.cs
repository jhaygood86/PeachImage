namespace PeachImage.Formats.Webp.Kernels;

/// <summary>
/// Hardware-tier-dispatched kernels for VP8L transform *forward* operations that have no cross-pixel
/// dependency — selected once at startup by <see cref="Vp8LEncodeKernelSelector"/>, mirroring
/// <see cref="IVp8LTransformKernel"/>'s decode-side dispatch pattern. Kept as a separate interface from
/// <see cref="IVp8LTransformKernel"/> rather than extending it: that one is explicitly scoped to inverse
/// (decode) operations, and the two directions have a real asymmetry worth not blurring together — every
/// predictor mode's *forward* residual computation reads only from the original, fully-available source
/// image (no "already reconstructed" pixel to wait for), so unlike decode (which only vectorizes mode 2),
/// every mode is embarrassingly parallel on the encode side.
/// </summary>
internal interface IVp8LEncodeKernel
{
    /// <summary>In place, per pixel: <c>red -= green; blue -= green;</c> (mod 256, no clamp) — the subtract-green transform's forward direction.</summary>
    void SubtractGreenForward(Span<uint> pixels);

    /// <summary>
    /// Writes <paramref name="residual"/><c>[i] = row[i] - topRow[i]</c> for every raw ARGB byte (mod 256, no
    /// clamp) — the predictor transform's mode 2 ("top") forward residual, applied to a whole row at once and
    /// written to a separate destination (rather than in place) since transform selection needs to compare
    /// this mode's cost against others before committing to it.
    /// </summary>
    void PredictorTopForward(ReadOnlySpan<byte> row, ReadOnlySpan<byte> topRow, Span<byte> residual);
}
