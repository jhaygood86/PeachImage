namespace PeachImage.Formats.Webp.Decoding.Vp8.Dct;

/// <summary>
/// VP8's 4x4 inverse Walsh-Hadamard transform (RFC 6386 section 14.3), used once per macroblock (when the
/// 16x16 luma mode is not B_PRED) to turn the Y2 block's 16 dequantized DC coefficients into the 16 individual
/// DC values that get scattered into each luma 4x4 subblock's own coefficient position 0, before that
/// subblock's own <see cref="Vp8ScalarInverseDct"/> runs.
/// </summary>
/// <remarks>
/// Transcribed verbatim (including the exact, easy-to-get-backwards index arithmetic and the final rounding
/// constant) from libwebp's <c>src/dsp/dec.c</c> <c>TransformWHT_C</c>, cross-checked against the downloaded
/// upstream source: pure integer add/subtract (no <see cref="Vp8InverseTransformConstants"/> scaling, unlike
/// the DCT), and the second pass's four outputs land at flat offsets <c>4*i + {0,1,2,3}</c> - i.e. in the same
/// raster order (row*4+col) as the 16 luma subblocks - not the sequential 0/1/2/3 layout of the coefficients
/// themselves.
/// </remarks>
internal static class Vp8ScalarInverseWht
{
    /// <summary>
    /// Transforms 16 dequantized Y2 coefficients (<paramref name="input"/>, natural raster order) into 16
    /// per-subblock DC values (<paramref name="blockDc"/>[<c>4*row+col</c>], matching the luma subblocks' own
    /// raster order).
    /// </summary>
    public static void Transform(ReadOnlySpan<short> input, Span<short> blockDc)
    {
        Span<int> tmp = stackalloc int[16];

        for (int i = 0; i < 4; i++)
        {
            int a0 = input[0 + i] + input[12 + i];
            int a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i];
            int a3 = input[0 + i] - input[12 + i];

            tmp[0 + i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }

        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[(i * 4) + 0] + 3;
            int a0 = dc + tmp[(i * 4) + 3];
            int a1 = tmp[(i * 4) + 1] + tmp[(i * 4) + 2];
            int a2 = tmp[(i * 4) + 1] - tmp[(i * 4) + 2];
            int a3 = dc - tmp[(i * 4) + 3];

            blockDc[(4 * i) + 0] = (short)((a0 + a1) >> 3);
            blockDc[(4 * i) + 1] = (short)((a3 + a2) >> 3);
            blockDc[(4 * i) + 2] = (short)((a0 - a1) >> 3);
            blockDc[(4 * i) + 3] = (short)((a3 - a2) >> 3);
        }
    }
}