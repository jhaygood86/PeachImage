using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1.Quantization;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Forward-quantizes AV1 transform coefficients, and maps a 0-100 <c>AvifEncoderOptions.Quality</c> to a
/// <c>base_q_idx</c> -- the write-side mirror of <see cref="Av1Dequantizer"/>, reusing its exact
/// <see cref="Av1QuantLookup"/> tables so a given <c>base_q_idx</c> means the same quantizer step on both
/// sides. Restricted to the non-quantizer-matrix path and the four square DCT_DCT sizes this v1 encoder
/// uses (see <see cref="Av1ForwardTransform"/>). A faithful port of libaom's own real
/// <c>av1_quantize_fp_no_qmatrix</c>/<c>av1_build_quantizer</c> (<c>av1/encoder/av1_quantize.c</c>) --
/// <em>not</em> the reciprocal-multiply-and-round approximation this method used before the libaom
/// test-porting round: that approximation only happened to be byte-identical to the real algorithm for
/// this project's own currently-reachable production case (lossless, <c>base_q_idx == 0</c>, where
/// <c>dcQ == acQ == 4</c> divides every WHT-scaled coefficient exactly, with no deadzone/rounding
/// edge case ever in play) -- confirmed by hand-deriving both formulas over that exact input space before
/// replacing it, so this change is a real, general-case correctness fix with zero behavioral change to any
/// output this project's own encoder currently produces, not a risky swap.
/// </summary>
internal static class Av1ForwardQuantizer
{
    /// <summary>
    /// Quantizes <paramref name="coeff"/> (a flat <paramref name="size"/> x <paramref name="size"/>
    /// row-major buffer, the output of <see cref="Av1ForwardTransform.Forward2D"/>) into
    /// <paramref name="levelsOut"/> (same shape), the integer levels that get entropy-coded.
    /// </summary>
    public static void Quantize(int[] coeff, int[] levelsOut, int size, int baseQIdx)
    {
        int txSz = Av1ForwardTransform.SizeToTxSz(size);
        int dcQ = Av1Dequantizer.DcQ(baseQIdx, 8);
        int acQ = Av1Dequantizer.AcQ(baseQIdx, 8);
        int logScale = txSz == Av1TxSize.Tx32x32 ? 1 : 0;

        // av1_build_quantizer's own real quant_fp/round_fp formulas (sharpness == 0, this project's own
        // only supported case -- see Av1ForwardQuantizer's own class remarks): quant_fp is a Q16 fixed-point
        // reciprocal of the dequant step, round_fp is a plain half-step rounding offset scaled by 64/128.
        int quantFpDc = (1 << 16) / dcQ;
        int quantFpAc = (1 << 16) / acQ;
        int roundFpDc = (64 * dcQ) >> 7;
        int roundFpAc = (64 * acQ) >> 7;

        Av1QuantizeKernelSelector.Instance.Quantize(coeff, levelsOut, size, quantFpDc, quantFpAc, roundFpDc, roundFpAc, dcQ, acQ, logScale);
    }

    /// <summary>
    /// Maps a 0-100 <c>AvifEncoderOptions.Quality</c> value to a 1-255 <c>base_q_idx</c> (never 0,
    /// which would trigger AV1's coded-lossless path -- see <see cref="Av1FrameHeaderWriter.Write"/>). A
    /// faithful port of the real formula chain <c>avifenc</c>/libavif and libaom actually use -- <em>not</em>
    /// the simple linear interpolation this method used before the libaom test-porting round, which was an
    /// independently-invented curve with no relationship to either project's own real one: libavif's own
    /// <c>aomQualityToQuantizer</c> (<c>src/codec_aom.c</c>, the default, non-<c>tune=iq</c> branch --
    /// <c>tune=iq</c>'s own separate piecewise table is a distinct, newer libaom &gt;= 3.13 feature this
    /// project doesn't otherwise model) maps quality 0-100 to a 0-63 "quantizer"/cq-level
    /// (<c>((100 - quality) * 63 + 50) / 100</c>), which libaom's own real <c>av1_quantizer_to_qindex</c>
    /// (<c>av1/encoder/av1_quantize.c</c>) then maps to the true 0-255 <c>base_q_idx</c> via a real lookup
    /// table -- <em>not</em> a plain <c>*4</c>: every entry from 0-61 is exactly quantizer*4, but the top two
    /// (62, 63) deviate (249, 255 instead of the "expected" 248, 252) specifically so the real 0-63 quantizer
    /// range's own ceiling lands exactly on AV1's own true qindex ceiling (255), not four short of it.
    /// Quality 100 reaching this method (i.e. without the separate <c>lossless</c> flag also being set, which
    /// is libavif's own real, entirely different code path for true lossless -- see its own
    /// <c>AVIF_QUALITY_LOSSLESS</c> branch) would otherwise map to qindex 0 (the formula's own real,
    /// mathematically correct output for quality 100) -- clamped to 1 here, preserving this method's own
    /// pre-existing contract ("never 0") for its own only real caller (<see cref="Av1FrameEncoder"/>, which
    /// only ever calls this for the non-lossless path).
    /// </summary>
    public static int QualityToBaseQIdx(int quality)
    {
        int baseQIdx = QuantizerToQindex[QualityToQuantizer(quality)];
        return Math.Max(baseQIdx, 1);
    }

    /// <summary>
    /// The real 0-63 libaom quantizer/cq-level this method's own first stage maps <paramref name="quality"/>
    /// to, before <see cref="QuantizerToQindex"/>'s own lookup -- exposed separately (not just inlined into
    /// <see cref="QualityToBaseQIdx"/>) so a caller driving real <c>aomenc</c> at a matching setting can pass
    /// this exact value to its own <c>--min-q</c>/<c>--max-q</c> (also a real 0-63 quantizer scale, not the
    /// raw 0-255 qindex -- confirmed directly: <c>aomenc</c> rejects a qindex there with "rc_max_quantizer
    /// out of range [..63]") instead of re-deriving or guessing it.
    /// </summary>
    public static int QualityToQuantizer(int quality)
    {
        int clampedQuality = Math.Clamp(quality, 0, 100);
        return (((100 - clampedQuality) * 63) + 50) / 100;
    }

    /// <summary><c>quantizer_to_qindex</c> (<c>av1/encoder/av1_quantize.c</c>), libaom's own real cq-level (0-63) to qindex (0-255) lookup table.</summary>
    private static readonly int[] QuantizerToQindex =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48,
        52, 56, 60, 64, 68, 72, 76, 80, 84, 88, 92, 96, 100,
        104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 148, 152,
        156, 160, 164, 168, 172, 176, 180, 184, 188, 192, 196, 200, 204,
        208, 212, 216, 220, 224, 228, 232, 236, 240, 244, 249, 255,
    ];
}
