using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Dequantizes and inverse-transforms quantized levels, adds the residual to a prediction buffer in place,
/// and clamps -- the exact same operation <see cref="Av1TileDecoder"/>'s private <c>Reconstruct()</c>
/// performs: <c>txType</c>'s transform (defaulting to DCT_DCT -- luma's only transform type, and chroma's
/// whenever its searched <c>uv_mode</c> is DC_PRED), or lossless Walsh-Hadamard when <c>lossless</c> is set.
/// This is not optional plumbing: AV1 intra prediction reads back <em>reconstructed</em> neighbor samples,
/// never source samples, so the RDO search and the final encode pass must maintain a reconstruction buffer
/// that is bit-identical to what a real decoder will later reconstruct from the same quantized levels --
/// get this wrong and the encoder's own predictions (and the bitstream it emits) silently desync from what
/// any real AV1 decoder reproduces.
/// </summary>
internal static class Av1LocalReconstructor
{
    /// <summary>
    /// Reconstructs one <paramref name="size"/> x <paramref name="size"/> block at <c>(x, y)</c> into
    /// <paramref name="plane"/> (a full-plane buffer of stride <paramref name="planeStride"/>): dequantizes
    /// <paramref name="quantLevels"/>, inverse-transforms, adds onto the existing prediction already
    /// written into <paramref name="plane"/> at that location, and clamps to <c>[0, 255]</c>.
    /// </summary>
    /// <param name="plane">The full-plane buffer to reconstruct into, of stride <paramref name="planeStride"/>.</param>
    /// <param name="planeStride">The row stride of <paramref name="plane"/>, in elements.</param>
    /// <param name="x">The block's left edge within <paramref name="plane"/>.</param>
    /// <param name="y">The block's top edge within <paramref name="plane"/>.</param>
    /// <param name="size">The block's width and height (this v1 encoder only ever reconstructs square blocks).</param>
    /// <param name="quantLevels">The block's quantized coefficient levels, flat <paramref name="size"/> x <paramref name="size"/> row-major.</param>
    /// <param name="baseQIdx">The frame's base quantizer index.</param>
    /// <param name="dequantScratch">
    /// Caller-owned scratch buffer for the dequantized coefficients, at least 64*64 elements (the fixed
    /// 64-column stride <see cref="Av1Dequantizer.Dequantize"/> and <see cref="Av1InverseTransform.Inverse2D"/>
    /// both use regardless of the actual transform size -- see their remarks). Every element
    /// <see cref="Av1InverseTransform.Inverse2D"/> reads back was just written by <see cref="Av1Dequantizer.Dequantize"/>
    /// in this same call (both bound their [i,j] access to <c>i &lt; size &amp;&amp; j &lt; size</c>), so the
    /// buffer's contents from a previous call never leak in -- no zeroing needed between calls.
    /// </param>
    /// <param name="residualScratch">Caller-owned scratch buffer for the inverse-transformed residual, at least <paramref name="size"/>*<paramref name="size"/> elements.</param>
    /// <param name="lossless">
    /// When <see langword="true"/>, inverse-transforms via AV1's lossless Walsh-Hadamard path instead of
    /// <paramref name="txType"/> (<paramref name="size"/> must be 4 -- AV1 lossless forces <c>TX_4X4</c> for
    /// every block) and <paramref name="baseQIdx"/> must be 0, matching <see cref="Av1ForwardWht"/>'s own
    /// pairing.
    /// </param>
    /// <param name="txType">
    /// The transform type <see cref="Av1InverseTransform.Inverse2D"/> uses when <paramref name="lossless"/>
    /// is <see langword="false"/> -- must match whatever <see cref="Av1ForwardTransform.Forward2D"/> (or,
    /// for luma, the caller's own tx_type choice) actually forward-transformed <paramref name="quantLevels"/>
    /// with. Defaults to <see cref="Av1TxType.DctDct"/>, this encoder's only luma transform type and (before
    /// chroma's real <c>uv_mode</c> search) its only chroma one too; a non-lossless chroma leaf whose
    /// searched <c>uv_mode</c> maps to a mixed ADST type (<c>Av1TxTypeTables.ModeToTxfm</c>) must pass that
    /// type explicitly -- getting this wrong doesn't desync the bitstream (chroma's tx_type is derived, never
    /// signalled), but does desync this encoder's own local reconstruction buffer from what a real decoder
    /// reconstructs, corrupting every later block's prediction that reads this one as neighbor context.
    /// </param>
    /// <remarks>
    /// Both scratch buffers exist so callers (<see cref="Av1TileEncoder"/>) can rent them once per tile and
    /// reuse them across every block, rather than this method allocating fresh per call -- previously a
    /// 16 KB heap allocation on <em>every single block</em> in the image (see git history for the allocation
    /// profile that motivated this).
    /// </remarks>
    public static void Reconstruct(int[] plane, int planeStride, int x, int y, int size, int[] quantLevels, int baseQIdx, int[] dequantScratch, int[] residualScratch, bool lossless = false, int txType = Av1TxType.DctDct)
    {
        int txSz = Av1ForwardTransform.SizeToTxSz(size);
        int dcQ = Av1Dequantizer.DcQ(baseQIdx, 8);
        int acQ = Av1Dequantizer.AcQ(baseQIdx, 8);

        Av1Dequantizer.Dequantize(quantLevels, dequantScratch, txSz, dcQ, acQ, 8);

        Av1InverseTransform.Inverse2D(dequantScratch, residualScratch, txSz, txType, lossless, bitDepth: 8);

        for (int i = 0; i < size; i++)
        {
            int rowBase = ((y + i) * planeStride) + x;
            int resBase = i * size;
            for (int j = 0; j < size; j++)
            {
                int idx = rowBase + j;
                plane[idx] = Math.Clamp(plane[idx] + residualScratch[resBase + j], 0, 255);
            }
        }
    }
}
