using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Dequantizes and inverse-transforms quantized levels, adds the residual to a prediction buffer in place,
/// and clamps -- the exact same operation <see cref="Av1TileDecoder"/>'s private <c>Reconstruct()</c>
/// performs (restricted to the DCT_DCT / no-FLIPADST case, the only one this v1 encoder ever produces).
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
    public static void Reconstruct(int[] plane, int planeStride, int x, int y, int size, int[] quantLevels, int baseQIdx)
    {
        int txSz = Av1ForwardTransform.SizeToTxSz(size);
        int dcQ = Av1Dequantizer.DcQ(baseQIdx, 8);
        int acQ = Av1Dequantizer.AcQ(baseQIdx, 8);

        var dequant = new int[64 * 64];
        Av1Dequantizer.Dequantize(quantLevels, dequant, txSz, dcQ, acQ, 8);

        var residual = new int[size * size];
        Av1InverseTransform.Inverse2D(dequant, residual, txSz, Av1TxType.DctDct, lossless: false, bitDepth: 8);

        for (int i = 0; i < size; i++)
        {
            int rowBase = ((y + i) * planeStride) + x;
            int resBase = i * size;
            for (int j = 0; j < size; j++)
            {
                int idx = rowBase + j;
                plane[idx] = Math.Clamp(plane[idx] + residual[resBase + j], 0, 255);
            }
        }
    }
}
