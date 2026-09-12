namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Direct, independent transcription of libaom's own real fixed-point inverse DCT butterfly networks --
/// <c>av1_idct4</c>/<c>av1_idct8</c>/<c>av1_idct16</c> (<c>av1/common/av1_inv_txfm1d.c</c>), plus the
/// <c>half_btf</c>/<c>clamp_value</c>/<c>round_shift</c> primitives and the Q12 <c>cospi</c> table
/// (<c>av1_cospi_arr_data[2]</c>, i.e. <c>cospi_arr(12)</c> -- <c>av1/common/av1_inv_txfm1d_cfg.h</c>'s own
/// <c>INV_COS_BIT</c> is 12) they read from (<c>av1/common/av1_txfm.c</c>/<c>av1_txfm.h</c>), kept
/// deliberately separate from <see cref="PeachImage.Formats.Avif.Decoding.Av1.Av1InverseTransform"/>'s own
/// implementation so a test comparing the two is a genuine check against libaom's real algorithm rather than
/// a self-consistency check. This is structurally a <em>different</em> transcription of the same normative
/// process than <c>Av1InverseTransform.InverseDct</c>: PeachImage's own class transcribes the AV1
/// specification's size-generic 31-step butterfly network (spec §7.13.2.3, one function covering every size
/// 4-64 via <c>if (n &gt;= k)</c> guards), while this class transcribes libaom's own size-specific C functions
/// (<c>av1_idct4</c>/<c>8</c>/<c>16</c>, each hand-unrolled with its own stage count and butterfly indices) --
/// two independently-authored implementations of the same bitstream-normative process, cross-checked here for
/// bit-exact agreement. (The spec and libaom's reference decoder necessarily compute identical results for any
/// conformant bitstream, since AV1's inverse transform is normative -- a real disagreement here means one of
/// the two *transcriptions* has a bug, not that the underlying algorithms differ.)
///
/// <para><b>Clamp-range simplification, deliberate and matching PeachImage's own choice:</b> libaom's real
/// <c>av1_idct4</c>/<c>8</c>/<c>16</c> take a full <c>stage_range[]</c> array (one clamp bit-width per stage,
/// computed by <c>av1_gen_inv_stage_range</c> from bit depth/size/stage index) so each intermediate addition
/// clamps to a stage-specific width. <see cref="Av1InverseTransform.InverseDct"/> does not reproduce that
/// per-stage table at all -- consistent with the AV1 <em>specification</em> text itself (§7.13.2.1's
/// <c>H(a, b, flip, r)</c> takes one clamp range <c>r</c> for the whole transform call, not a per-step table;
/// the spec only needs to bound the final conformant range, not match an encoder-side optimization's tighter
/// intermediate bounds). This class follows the same single-<paramref name="r"/>-per-call convention as
/// <see cref="Av1InverseTransform.InverseDct"/>'s own <c>r</c> parameter (itself the spec's <c>r</c>), passing
/// it as libaom's uniform <c>stage_range[stage]</c> at every clamp site instead of a real per-stage table. For
/// any input whose magnitude never approaches the clamp boundary at any stage -- true of every coefficient
/// this test feeds in -- a tighter, stage-varying bound and this single uniform bound produce identical
/// results, so this simplification does not change what bit-exactness against
/// <see cref="Av1InverseTransform.InverseDct"/> demonstrates.</para>
/// </summary>
internal static class LibaomReferenceDct
{
    /// <summary><c>cospi_arr(12)</c> (<c>av1_cospi_arr_data[2]</c>, <c>av1/common/av1_txfm.c</c>) -- the Q12 cosine table libaom's real inverse-DCT butterflies read via <c>cospi[k]</c>. Identical in value to <see cref="Av1InverseTransform"/>'s own <c>Cos128Lookup[0..63]</c> (that array additionally carries <c>cospi[64] == 0</c>, unused by any <c>half_btf</c> call site here), confirming both transcriptions read the same underlying spec table (§7.13.2.1's <c>Cos128_Lookup</c>) even though this class was typed independently from the real libaom C source, not from PeachImage's own array.</summary>
    private static readonly int[] Cospi =
    [
        4096, 4095, 4091, 4085, 4076, 4065, 4052, 4036,
        4017, 3996, 3973, 3948, 3920, 3889, 3857, 3822,
        3784, 3745, 3703, 3659, 3612, 3564, 3513, 3461,
        3406, 3349, 3290, 3229, 3166, 3102, 3035, 2967,
        2896, 2824, 2751, 2675, 2598, 2520, 2440, 2359,
        2276, 2191, 2106, 2019, 1931, 1842, 1751, 1660,
        1567, 1474, 1380, 1285, 1189, 1092, 995, 897,
        799, 700, 601, 501, 401, 301, 201, 101,
    ];

    private const int CosBit = 12; // INV_COS_BIT, av1/common/av1_inv_txfm1d_cfg.h.

    /// <summary><c>half_btf(w0, in0, w1, in1, bit)</c> (av1/common/av1_txfm.h) -- <see langword="long"/> intermediate exactly as the real <c>int64_t result_64</c>/<c>intermediate</c> locals.</summary>
    private static int HalfBtf(int w0, int in0, int w1, int in1)
    {
        long result64 = ((long)w0 * in0) + ((long)w1 * in1);
        long intermediate = result64 + (1L << (CosBit - 1));
        return (int)(intermediate >> CosBit);
    }

    /// <summary><c>clamp_value(value, bit)</c> (av1/common/av1_inv_txfm1d.h): a no-op for <paramref name="bit"/> &lt;= 0, otherwise a symmetric-ish clamp to the signed <paramref name="bit"/>-width range.</summary>
    private static int ClampValue(long value, int bit)
    {
        if (bit <= 0)
        {
            return (int)value;
        }

        long max = (1L << (bit - 1)) - 1;
        long min = -(1L << (bit - 1));
        return (int)Math.Clamp(value, min, max);
    }

    /// <summary>Bit-exact port of libaom's real <c>av1_idct4</c> (av1/common/av1_inv_txfm1d.c).</summary>
    public static void Idct4(ReadOnlySpan<int> input, Span<int> output, int r)
    {
        Span<int> step = stackalloc int[4];

        // stage 1
        output[0] = input[0];
        output[1] = input[2];
        output[2] = input[1];
        output[3] = input[3];

        // stage 2
        step[0] = HalfBtf(Cospi[32], output[0], Cospi[32], output[1]);
        step[1] = HalfBtf(Cospi[32], output[0], -Cospi[32], output[1]);
        step[2] = HalfBtf(Cospi[48], output[2], -Cospi[16], output[3]);
        step[3] = HalfBtf(Cospi[16], output[2], Cospi[48], output[3]);

        // stage 3
        output[0] = ClampValue(step[0] + step[3], r);
        output[1] = ClampValue(step[1] + step[2], r);
        output[2] = ClampValue(step[1] - step[2], r);
        output[3] = ClampValue(step[0] - step[3], r);
    }

    /// <summary>Bit-exact port of libaom's real <c>av1_idct8</c> (av1/common/av1_inv_txfm1d.c).</summary>
    public static void Idct8(ReadOnlySpan<int> input, Span<int> output, int r)
    {
        Span<int> step = stackalloc int[8];

        // stage 1
        output[0] = input[0];
        output[1] = input[4];
        output[2] = input[2];
        output[3] = input[6];
        output[4] = input[1];
        output[5] = input[5];
        output[6] = input[3];
        output[7] = input[7];

        // stage 2
        step[0] = output[0];
        step[1] = output[1];
        step[2] = output[2];
        step[3] = output[3];
        step[4] = HalfBtf(Cospi[56], output[4], -Cospi[8], output[7]);
        step[5] = HalfBtf(Cospi[24], output[5], -Cospi[40], output[6]);
        step[6] = HalfBtf(Cospi[40], output[5], Cospi[24], output[6]);
        step[7] = HalfBtf(Cospi[8], output[4], Cospi[56], output[7]);

        // stage 3
        output[0] = HalfBtf(Cospi[32], step[0], Cospi[32], step[1]);
        output[1] = HalfBtf(Cospi[32], step[0], -Cospi[32], step[1]);
        output[2] = HalfBtf(Cospi[48], step[2], -Cospi[16], step[3]);
        output[3] = HalfBtf(Cospi[16], step[2], Cospi[48], step[3]);
        output[4] = ClampValue(step[4] + step[5], r);
        output[5] = ClampValue(step[4] - step[5], r);
        output[6] = ClampValue(-step[6] + step[7], r);
        output[7] = ClampValue(step[6] + step[7], r);

        // stage 4
        step[0] = ClampValue(output[0] + output[3], r);
        step[1] = ClampValue(output[1] + output[2], r);
        step[2] = ClampValue(output[1] - output[2], r);
        step[3] = ClampValue(output[0] - output[3], r);
        step[4] = output[4];
        step[5] = HalfBtf(-Cospi[32], output[5], Cospi[32], output[6]);
        step[6] = HalfBtf(Cospi[32], output[5], Cospi[32], output[6]);
        step[7] = output[7];

        // stage 5
        output[0] = ClampValue(step[0] + step[7], r);
        output[1] = ClampValue(step[1] + step[6], r);
        output[2] = ClampValue(step[2] + step[5], r);
        output[3] = ClampValue(step[3] + step[4], r);
        output[4] = ClampValue(step[3] - step[4], r);
        output[5] = ClampValue(step[2] - step[5], r);
        output[6] = ClampValue(step[1] - step[6], r);
        output[7] = ClampValue(step[0] - step[7], r);
    }

    /// <summary>Bit-exact port of libaom's real <c>av1_idct16</c> (av1/common/av1_inv_txfm1d.c).</summary>
    public static void Idct16(ReadOnlySpan<int> input, Span<int> output, int r)
    {
        Span<int> step = stackalloc int[16];

        // stage 1
        output[0] = input[0];
        output[1] = input[8];
        output[2] = input[4];
        output[3] = input[12];
        output[4] = input[2];
        output[5] = input[10];
        output[6] = input[6];
        output[7] = input[14];
        output[8] = input[1];
        output[9] = input[9];
        output[10] = input[5];
        output[11] = input[13];
        output[12] = input[3];
        output[13] = input[11];
        output[14] = input[7];
        output[15] = input[15];

        // stage 2
        step[0] = output[0];
        step[1] = output[1];
        step[2] = output[2];
        step[3] = output[3];
        step[4] = output[4];
        step[5] = output[5];
        step[6] = output[6];
        step[7] = output[7];
        step[8] = HalfBtf(Cospi[60], output[8], -Cospi[4], output[15]);
        step[9] = HalfBtf(Cospi[28], output[9], -Cospi[36], output[14]);
        step[10] = HalfBtf(Cospi[44], output[10], -Cospi[20], output[13]);
        step[11] = HalfBtf(Cospi[12], output[11], -Cospi[52], output[12]);
        step[12] = HalfBtf(Cospi[52], output[11], Cospi[12], output[12]);
        step[13] = HalfBtf(Cospi[20], output[10], Cospi[44], output[13]);
        step[14] = HalfBtf(Cospi[36], output[9], Cospi[28], output[14]);
        step[15] = HalfBtf(Cospi[4], output[8], Cospi[60], output[15]);

        // stage 3
        output[0] = step[0];
        output[1] = step[1];
        output[2] = step[2];
        output[3] = step[3];
        output[4] = HalfBtf(Cospi[56], step[4], -Cospi[8], step[7]);
        output[5] = HalfBtf(Cospi[24], step[5], -Cospi[40], step[6]);
        output[6] = HalfBtf(Cospi[40], step[5], Cospi[24], step[6]);
        output[7] = HalfBtf(Cospi[8], step[4], Cospi[56], step[7]);
        output[8] = ClampValue(step[8] + step[9], r);
        output[9] = ClampValue(step[8] - step[9], r);
        output[10] = ClampValue(-step[10] + step[11], r);
        output[11] = ClampValue(step[10] + step[11], r);
        output[12] = ClampValue(step[12] + step[13], r);
        output[13] = ClampValue(step[12] - step[13], r);
        output[14] = ClampValue(-step[14] + step[15], r);
        output[15] = ClampValue(step[14] + step[15], r);

        // stage 4
        step[0] = HalfBtf(Cospi[32], output[0], Cospi[32], output[1]);
        step[1] = HalfBtf(Cospi[32], output[0], -Cospi[32], output[1]);
        step[2] = HalfBtf(Cospi[48], output[2], -Cospi[16], output[3]);
        step[3] = HalfBtf(Cospi[16], output[2], Cospi[48], output[3]);
        step[4] = ClampValue(output[4] + output[5], r);
        step[5] = ClampValue(output[4] - output[5], r);
        step[6] = ClampValue(-output[6] + output[7], r);
        step[7] = ClampValue(output[6] + output[7], r);
        step[8] = output[8];
        step[9] = HalfBtf(-Cospi[16], output[9], Cospi[48], output[14]);
        step[10] = HalfBtf(-Cospi[48], output[10], -Cospi[16], output[13]);
        step[11] = output[11];
        step[12] = output[12];
        step[13] = HalfBtf(-Cospi[16], output[10], Cospi[48], output[13]);
        step[14] = HalfBtf(Cospi[48], output[9], Cospi[16], output[14]);
        step[15] = output[15];

        // stage 5
        output[0] = ClampValue(step[0] + step[3], r);
        output[1] = ClampValue(step[1] + step[2], r);
        output[2] = ClampValue(step[1] - step[2], r);
        output[3] = ClampValue(step[0] - step[3], r);
        output[4] = step[4];
        output[5] = HalfBtf(-Cospi[32], step[5], Cospi[32], step[6]);
        output[6] = HalfBtf(Cospi[32], step[5], Cospi[32], step[6]);
        output[7] = step[7];
        output[8] = ClampValue(step[8] + step[11], r);
        output[9] = ClampValue(step[9] + step[10], r);
        output[10] = ClampValue(step[9] - step[10], r);
        output[11] = ClampValue(step[8] - step[11], r);
        output[12] = ClampValue(-step[12] + step[15], r);
        output[13] = ClampValue(-step[13] + step[14], r);
        output[14] = ClampValue(step[13] + step[14], r);
        output[15] = ClampValue(step[12] + step[15], r);

        // stage 6
        step[0] = ClampValue(output[0] + output[7], r);
        step[1] = ClampValue(output[1] + output[6], r);
        step[2] = ClampValue(output[2] + output[5], r);
        step[3] = ClampValue(output[3] + output[4], r);
        step[4] = ClampValue(output[3] - output[4], r);
        step[5] = ClampValue(output[2] - output[5], r);
        step[6] = ClampValue(output[1] - output[6], r);
        step[7] = ClampValue(output[0] - output[7], r);
        step[8] = output[8];
        step[9] = output[9];
        step[10] = HalfBtf(-Cospi[32], output[10], Cospi[32], output[13]);
        step[11] = HalfBtf(-Cospi[32], output[11], Cospi[32], output[12]);
        step[12] = HalfBtf(Cospi[32], output[11], Cospi[32], output[12]);
        step[13] = HalfBtf(Cospi[32], output[10], Cospi[32], output[13]);
        step[14] = output[14];
        step[15] = output[15];

        // stage 7
        output[0] = ClampValue(step[0] + step[15], r);
        output[1] = ClampValue(step[1] + step[14], r);
        output[2] = ClampValue(step[2] + step[13], r);
        output[3] = ClampValue(step[3] + step[12], r);
        output[4] = ClampValue(step[4] + step[11], r);
        output[5] = ClampValue(step[5] + step[10], r);
        output[6] = ClampValue(step[6] + step[9], r);
        output[7] = ClampValue(step[7] + step[8], r);
        output[8] = ClampValue(step[7] - step[8], r);
        output[9] = ClampValue(step[6] - step[9], r);
        output[10] = ClampValue(step[5] - step[10], r);
        output[11] = ClampValue(step[4] - step[11], r);
        output[12] = ClampValue(step[3] - step[12], r);
        output[13] = ClampValue(step[2] - step[13], r);
        output[14] = ClampValue(step[1] - step[14], r);
        output[15] = ClampValue(step[0] - step[15], r);
    }
}
