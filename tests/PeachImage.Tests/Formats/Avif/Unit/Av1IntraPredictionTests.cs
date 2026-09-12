using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Port of libaom's own <c>intrapred_test.cc</c> <c>AV1IntraPredTest.RunTest</c> for the four 4x4,
/// both-neighbors-available basic predictors (DC_PRED, PAETH_PRED, SMOOTH_PRED, SMOOTH_V_PRED,
/// SMOOTH_H_PRED) -- a faithful port using <see cref="LibaomAcmRandom"/> (the same deterministic PRNG port
/// used for the WHT and Hadamard test ports) and <see cref="LibaomReferenceIntraPred"/> (transcribed
/// directly from <c>aom_dsp/intrapred.c</c>'s own <c>paeth_predictor</c>/<c>smooth_predictor</c>/
/// <c>smooth_v_predictor</c>/<c>smooth_h_predictor</c>/<c>dc_predictor</c>) as the reference, checked
/// against <see cref="Av1IntraPrediction.Predict"/> -- which was itself independently built from the AV1
/// spec's own §7.11.2 text (see <see cref="Av1IntraPrediction"/>'s own remarks), not from libaom's C source,
/// so this is a genuine two-independent-sources check, not a self-consistency check against one
/// transcription of the algorithm.
///
/// <para>Reduced from libaom's own real 100,000 iterations to 5,000 (matching the iteration count this
/// project's own other libaom test ports settled on) -- the iteration count only affects input coverage
/// breadth, not what "correct" means, and 5,000 draws already exercises the full random 8-bit residual
/// range many times over for a 4x4 block's 7 free values (above[-1..3], left[0..3]).</para>
/// </summary>
public class Av1IntraPredictionTests
{
    private const int CountTestBlock = 5000;

    [Fact]
    public void DcPred_MatchesLibaomReference()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        RunBothAvailable(rnd, Av1IntraMode.DcPred, (above, left, topLeft) => Fill(LibaomReferenceIntraPred.Dc(above, left)));
    }

    /// <summary>
    /// <c>dc_left_predictor</c>'s own real case (above unavailable) -- <see cref="Av1IntraPrediction.Predict"/>
    /// dispatched with <c>haveLeft: true, haveAbove: false</c>. libaom's own real function never reads
    /// <c>above</c> at all in this case, but the spec-derived port's own array-construction step
    /// (§7.11.2.1) still fills <c>AboveRow</c> with a synthetic default before <c>PredictDc</c> ever runs --
    /// exercising that the synthetic fill is genuinely unread by this code path, not merely unread by
    /// libaom's own.
    /// </summary>
    [Fact]
    public void DcPred_AboveUnavailable_MatchesLibaomReference()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        Run(rnd, Av1IntraMode.DcPred, haveLeft: true, haveAbove: false, (above, left, topLeft) => Fill(LibaomReferenceIntraPred.DcLeft(left)));
    }

    /// <summary><c>dc_top_predictor</c>'s own real case (left unavailable).</summary>
    [Fact]
    public void DcPred_LeftUnavailable_MatchesLibaomReference()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        Run(rnd, Av1IntraMode.DcPred, haveLeft: false, haveAbove: true, (above, left, topLeft) => Fill(LibaomReferenceIntraPred.DcTop(above)));
    }

    /// <summary><c>dc_128_predictor</c>'s own real case (neither neighbor available).</summary>
    [Fact]
    public void DcPred_NeitherAvailable_MatchesLibaomReference()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        Run(rnd, Av1IntraMode.DcPred, haveLeft: false, haveAbove: false, (above, left, topLeft) => Fill(LibaomReferenceIntraPred.Dc128(8)));
    }

    [Fact]
    public void PaethPred_MatchesLibaomReference()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        RunBothAvailable(rnd, Av1IntraMode.PaethPred, (above, left, topLeft) =>
        {
            var dst = new int[16];
            LibaomReferenceIntraPred.Paeth(above, left, topLeft, dst);
            return dst;
        });
    }

    [Fact]
    public void SmoothPred_MatchesLibaomReference()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        RunBothAvailable(rnd, Av1IntraMode.SmoothPred, (above, left, topLeft) =>
        {
            var dst = new int[16];
            LibaomReferenceIntraPred.Smooth(above, left, dst);
            return dst;
        });
    }

    [Fact]
    public void SmoothVPred_MatchesLibaomReference()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        RunBothAvailable(rnd, Av1IntraMode.SmoothVPred, (above, left, topLeft) =>
        {
            var dst = new int[16];
            LibaomReferenceIntraPred.SmoothV(above, left, dst);
            return dst;
        });
    }

    [Fact]
    public void SmoothHPred_MatchesLibaomReference()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        RunBothAvailable(rnd, Av1IntraMode.SmoothHPred, (above, left, topLeft) =>
        {
            var dst = new int[16];
            LibaomReferenceIntraPred.SmoothH(above, left, dst);
            return dst;
        });
    }

    private static int[] Fill(int value)
    {
        var dst = new int[16];
        Array.Fill(dst, value);
        return dst;
    }

    /// <summary>
    /// Mirrors <c>AV1IntraPredTest.RunTest</c>'s own edge-generation loop: the very first block is all-mask
    /// (255, the saturated case libaom's own test specifically calls out), every subsequent block is
    /// <c>Rand16() &amp; mask_</c> per edge sample (8-bit, mask = 255) -- both neighbors always available,
    /// matching every reference function above's own "both available" real signature.
    /// </summary>
    private static void RunBothAvailable(LibaomAcmRandom rnd, int mode, Func<int[], int[], int, int[]> reference)
        => Run(rnd, mode, haveLeft: true, haveAbove: true, reference);

    /// <summary>
    /// Generalized form of <see cref="RunBothAvailable"/> allowing either neighbor to be marked unavailable
    /// (dispatching to <see cref="Av1IntraPrediction.Predict"/>'s own <c>haveLeft</c>/<c>haveAbove</c> branch
    /// selection, matching libaom's own separate <c>dc_left_predictor</c>/<c>dc_top_predictor</c>/
    /// <c>dc_128_predictor</c> functions), still generating full random edge data regardless (an unavailable
    /// side's own array values are simply never read by either the reference or the code under test, so
    /// generating them anyway costs nothing and keeps the RNG draw pattern uniform across every test method).
    /// </summary>
    private static void Run(LibaomAcmRandom rnd, int mode, bool haveLeft, bool haveAbove, Func<int[], int[], int, int[]> reference)
    {
        var pred = new int[16];
        var aboveRow = new Av1EdgeArray(4);
        var leftCol = new Av1EdgeArray(4);

        for (int i = 0; i < CountTestBlock; i++)
        {
            int topLeft;
            var above = new int[4];
            var left = new int[4];

            if (i == 0)
            {
                topLeft = 255;
                for (int x = 0; x < 4; x++)
                {
                    above[x] = 255;
                    left[x] = 255;
                }
            }
            else
            {
                topLeft = rnd.Rand16() & 255;
                for (int x = 0; x < 4; x++)
                {
                    above[x] = rnd.Rand16() & 255;
                }

                for (int y = 0; y < 4; y++)
                {
                    left[y] = rnd.Rand16() & 255;
                }
            }

            aboveRow[-1] = topLeft;
            for (int x = 0; x < 4; x++)
            {
                aboveRow[x] = above[x];
            }

            for (int y = 0; y < 4; y++)
            {
                leftCol[y] = left[y];
            }

            Av1IntraPrediction.Predict(
                pred, w: 4, h: 4, log2W: 2, log2H: 2, aboveRow, leftCol, mode,
                haveLeft, haveAbove, useFilterIntra: false, filterIntraMode: 0, angleDelta: 0,
                enableIntraEdgeFilter: false, filterTypeSmooth: false, maxX: 3, maxY: 3, x: 0, y: 0, bitDepth: 8);

            int[] expected = reference(above, left, topLeft);

            for (int k = 0; k < 16; k++)
            {
                Assert.True(expected[k] == pred[k], $"block {i}, position {k}: expected {expected[k]}, actual {pred[k]}");
            }
        }
    }
}
