namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>Dequantization (spec §7.12.2-§7.12.3), restricted to the non-quantizer-matrix path (<c>using_qmatrix</c> is rejected at frame-header parse time).</summary>
internal static class Av1Dequantizer
{
    /// <summary><c>dc_q(b)</c> (spec §7.12.2).</summary>
    public static int DcQ(int b, int bitDepth) => Av1QuantLookup.DcQLookup[(bitDepth - 8) >> 1][Math.Clamp(b, 0, 255)];

    /// <summary><c>ac_q(b)</c> (spec §7.12.2).</summary>
    public static int AcQ(int b, int bitDepth) => Av1QuantLookup.AcQLookup[(bitDepth - 8) >> 1][Math.Clamp(b, 0, 255)];

    /// <summary>
    /// The dequantization half of <c>reconstruct()</c> (spec §7.12.3, step 1): scales <paramref name="quant"/>
    /// (indexed <c>[i * tw + j]</c>, row-major within the coded <c>Min(32,w) x Min(32,h)</c> region) into
    /// <paramref name="dequant"/> (a flat <c>64x64</c> buffer, row-major, indexed <c>[i * 64 + j]</c>).
    /// <para>
    /// <paramref name="nzRows"/>/<paramref name="nzCols"/> bound the region that can hold non-zero
    /// coefficients (callers that know the block's last significant row/column pass them; the defaults cover
    /// the whole coded region). Only <c>[0, nzRows) x [0, nzCols)</c> is written, so the consumer must read
    /// nothing outside it -- <see cref="Av1InverseTransform.Inverse2D"/> takes the same two bounds.
    /// </para>
    /// </summary>
    public static void Dequantize(ReadOnlySpan<int> quant, int[] dequant, int txSz, int dcQuant, int acQuant, int bitDepth, int nzRows = 32, int nzCols = 32)
    {
        int dqDenom = txSz is Av1TxSize.Tx32x32 or Av1TxSize.Tx16x32 or Av1TxSize.Tx32x16 or Av1TxSize.Tx16x64 or Av1TxSize.Tx64x16
            ? 2
            : txSz is Av1TxSize.Tx64x64 or Av1TxSize.Tx32x64 or Av1TxSize.Tx64x32
                ? 4
                : 1;

        int w = Av1TxDimensions.Width[txSz];
        int h = Av1TxDimensions.Height[txSz];
        int tw = Math.Min(32, w);
        int th = Math.Min(32, h);

        long clampBound = 1L << (7 + bitDepth);

        int rows = Math.Min(th, nzRows);
        int cols = Math.Min(tw, nzCols);
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                int q = i == 0 && j == 0 ? dcQuant : acQuant;
                long dq = (long)quant[(i * tw) + j] * q;
                int sign = dq < 0 ? -1 : 1;
                long dq2 = sign * ((Math.Abs(dq) & 0xFFFFFF) / dqDenom);
                dequant[(i * 64) + j] = (int)Math.Clamp(dq2, -clampBound, clampBound - 1);
            }
        }
    }
}
