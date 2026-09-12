using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Faithful port of libaom's real <c>av1_get_deltaq_offset</c> (<c>av1/encoder/rd.c</c>): given a perceptual
/// quality-adjustment factor <c>beta</c> (a per-region "how much sharper/blurrier should this be" multiplier,
/// typically driven by a variance- or saliency-based model), finds the signed <c>qindex</c> offset that moves
/// the real DC quantizer step closest to <c>currentStep / sqrt(beta)</c> -- a linear search outward from the
/// current qindex toward the target step, clamped to the valid <c>[0, 255]</c> qindex
/// range, matching libaom's own real algorithm exactly (not a closed-form inverse of the real, non-linear
/// <see cref="Av1Dequantizer.DcQ"/> lookup table).
///
/// <para>Not yet wired into any real per-block decision: this project's own encoder has no perceptual
/// quality-adjustment model (the "delta-q mode" RD-search machinery that would compute a real
/// <c>beta</c> per superblock and apply this offset) -- porting that whole feature is future, separately
/// scoped work. This class ports the one small, pure, independently-testable primitive libaom's own real
/// <c>rd_test.cc</c> covers, verified correct and ready for that future work to call.</para>
/// </summary>
internal static class Av1DeltaQ
{
    private const int MaxQ = 255;

    /// <summary><c>av1_get_deltaq_offset</c> (<c>av1/encoder/rd.c</c>).</summary>
    public static int GetDeltaqOffset(int bitDepth, int qindex, double beta)
    {
        int q = Av1Dequantizer.DcQ(qindex, bitDepth);

        // C's rint() rounds using the current floating-point rounding mode, which defaults to
        // round-to-nearest-even on every real platform (never explicitly changed anywhere in libaom) --
        // matching C#'s own Math.Round default (MidpointRounding.ToEven), not AwayFromZero.
        int newQ = (int)Math.Round(q / Math.Sqrt(beta));
        int origQindex = qindex;

        if (newQ == q)
        {
            return 0;
        }

        if (newQ < q)
        {
            while (qindex > 0)
            {
                qindex--;
                q = Av1Dequantizer.DcQ(qindex, bitDepth);
                if (newQ >= q)
                {
                    break;
                }
            }
        }
        else
        {
            while (qindex < MaxQ)
            {
                qindex++;
                q = Av1Dequantizer.DcQ(qindex, bitDepth);
                if (newQ <= q)
                {
                    break;
                }
            }
        }

        return qindex - origQindex;
    }
}
