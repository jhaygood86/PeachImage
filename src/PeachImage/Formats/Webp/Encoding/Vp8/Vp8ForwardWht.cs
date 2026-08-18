namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// VP8's forward Walsh-Hadamard transform, the encode-side counterpart of
/// <see cref="Decoding.Vp8.Dct.Vp8ScalarInverseWht"/>: collects the 16 luma subblocks' own DCT DC coefficients
/// (already computed by <see cref="Vp8ForwardDct"/>, one per subblock, in the same raster order
/// <see cref="Decoding.Vp8.Dct.Vp8ScalarInverseWht"/>'s <c>blockDc</c> output uses) and transforms them into the
/// macroblock's single Y2 block. Transcribed verbatim (butterfly structure and final &gt;&gt;1 scaling, no
/// rounding bias) from libwebp's <c>src/dsp/enc.c</c> <c>FTransformWHT_C</c>, cross-checked against the
/// downloaded upstream source; re-expressed over a compact 16-element input/output span in raster
/// (<c>row*4+col</c>) order rather than libwebp's interleaved-stride read pattern over a full 256-element
/// coefficient array, since the algorithm is the same 2-pass integer butterfly either way and this codebase's
/// forward pipeline already holds each subblock's coefficients separately.
/// </summary>
internal static class Vp8ForwardWht
{
    /// <summary>Transforms 16 per-subblock DC values (<paramref name="input"/>[<c>4*row+col</c>]) into the Y2 block's 16 coefficients (<paramref name="output"/>, natural raster order, not zigzag).</summary>
    public static void Transform(ReadOnlySpan<short> input, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];

        for (int row = 0; row < 4; row++)
        {
            int c0 = input[(row * 4) + 0];
            int c1 = input[(row * 4) + 1];
            int c2 = input[(row * 4) + 2];
            int c3 = input[(row * 4) + 3];

            int a0 = c0 + c2;
            int a1 = c1 + c3;
            int a2 = c1 - c3;
            int a3 = c0 - c2;

            tmp[(row * 4) + 0] = a0 + a1;
            tmp[(row * 4) + 1] = a3 + a2;
            tmp[(row * 4) + 2] = a3 - a2;
            tmp[(row * 4) + 3] = a0 - a1;
        }

        for (int col = 0; col < 4; col++)
        {
            int t0 = tmp[(0 * 4) + col];
            int t1 = tmp[(1 * 4) + col];
            int t2 = tmp[(2 * 4) + col];
            int t3 = tmp[(3 * 4) + col];

            int a0 = t0 + t2;
            int a1 = t1 + t3;
            int a2 = t1 - t3;
            int a3 = t0 - t2;

            output[(0 * 4) + col] = (short)((a0 + a1) >> 1);
            output[(1 * 4) + col] = (short)((a3 + a2) >> 1);
            output[(2 * 4) + col] = (short)((a3 - a2) >> 1);
            output[(3 * 4) + col] = (short)((a0 - a1) >> 1);
        }
    }
}
