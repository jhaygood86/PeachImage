using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Port of libaom's own <c>filterintra_test.cc</c> <c>AV1FilterIntraPredTest.RunTest</c> for all 5 real
/// filter-intra submodes (FILTER_DC_PRED, FILTER_V_PRED, FILTER_H_PRED, FILTER_D157_PRED,
/// FILTER_PAETH_PRED) across the real test's own full <c>kTxSize</c> list of 14 transform sizes (every
/// square size 4x4..32x32 plus every 1:2/2:1/1:4/4:1 rectangular size libaom's own real test enumerates) --
/// a faithful port using <see cref="LibaomAcmRandom"/> (the same deterministic PRNG port used for the WHT,
/// Hadamard, and basic-intra-prediction test ports) and <see cref="LibaomReferenceFilterIntra"/>
/// (transcribed directly from <c>av1/common/reconintra.c</c>'s own <c>av1_filter_intra_predictor_c</c> and
/// its <c>av1_filter_intra_taps</c> table) as the reference, checked against
/// <see cref="Av1IntraPrediction.Predict"/>'s own <c>useFilterIntra: true</c> path (private
/// <c>PredictRecursive</c>) -- which was itself independently built from the AV1 spec's own §7.11.2.3 text
/// (see <see cref="LibaomReferenceFilterIntra"/>'s own remarks), not from libaom's C source, so this is a
/// genuine two-independent-sources check, not a self-consistency check against one transcription of the
/// algorithm.
///
/// <para>libaom's own real <c>PrepareBuffer</c> constructs a *new* <c>ACMRandom(DeterministicSeed())</c>
/// every single call -- including once per inner-loop iteration inside <c>RunTest</c> -- so the real test's
/// own 100 "iterations" (<c>MaxTestNum</c>) per (mode, tx size) combination all feed the exact same edge
/// buffer to both the reference and tested functions; the loop count is real, but it draws zero additional
/// random coverage after the very first pass. This port deliberately does not reproduce that specific
/// quirk: instead each (mode, tx size) combination gets its own <see cref="LibaomAcmRandom"/> seeded from
/// <see cref="LibaomAcmRandom.DeterministicSeed"/> (so the whole run stays fully deterministic and
/// reproducible) but left running across all <see cref="CountTestBlock"/> iterations, so every iteration
/// exercises genuinely different 8-bit edge data -- matching this project's own established practice
/// (see <c>Av1IntraPredictionTests</c>'s own iteration-count remarks) of preserving the real test's intent
/// (bit-exact match over randomized 8-bit input) rather than a literal quirk that adds no coverage.</para>
///
/// <para><see cref="CountTestBlock"/> matches libaom's own real <c>MaxTestNum</c> (100) -- unlike the
/// 100,000-iteration basic-intra-prediction test, 100 draws per combination times 5 modes times 14 sizes
/// (7,000 draws total, each redrawing up to 65 bytes) is already a substantial random-input sweep, so no
/// reduction was needed to keep this fast.</para>
/// </summary>
public class Av1FilterIntraPredictionTests
{
    private const int CountTestBlock = 100;
    private const int MaxTxSize = 32;

    /// <summary><c>FILTER_DC_PRED</c>/<c>FILTER_V_PRED</c>/<c>FILTER_H_PRED</c>/<c>FILTER_D157_PRED</c>/
    /// <c>FILTER_PAETH_PRED</c> (<c>av1/common/enums.h</c>) -- the 5 real <c>FILTER_INTRA_MODES</c> values,
    /// 0 through 4 in declaration order.</summary>
    private static readonly (string Name, int Mode)[] Modes =
    [
        ("FILTER_DC_PRED", 0),
        ("FILTER_V_PRED", 1),
        ("FILTER_H_PRED", 2),
        ("FILTER_D157_PRED", 3),
        ("FILTER_PAETH_PRED", 4),
    ];

    /// <summary>libaom's own real <c>kTxSize</c> array (<c>filterintra_test.cc</c>): every square TX size
    /// filter-intra supports plus every rectangular size up to 4:1/1:4.</summary>
    private static readonly (int Width, int Height)[] TxSizes =
    [
        (4, 4), (8, 8), (16, 16), (32, 32), (4, 8),
        (8, 4), (8, 16), (16, 8), (16, 32), (32, 16),
        (4, 16), (16, 4), (8, 32), (32, 8),
    ];

    public static IEnumerable<object[]> ModeAndSizeCases()
    {
        foreach (var (name, mode) in Modes)
        {
            foreach (var (w, h) in TxSizes)
            {
                yield return [name, mode, w, h];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ModeAndSizeCases))]
    public void FilterIntra_MatchesLibaomReference(string modeName, int mode, int w, int h)
    {
        // Seeded independently per (mode, size) combination so this test's own total draw count/ordering
        // doesn't shift a combination's random data if another combination is added or removed later.
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        var alloc = new byte[(2 * MaxTxSize) + 1];
        var above = new int[w];
        var left = new int[h];
        var expected = new int[w * h];
        var pred = new int[w * h];
        var aboveRow = new Av1EdgeArray(w);
        var leftCol = new Av1EdgeArray(h);

        for (int iter = 0; iter < CountTestBlock; iter++)
        {
            // Mirrors libaom's own real PrepareBuffer: left = alloc_[0..MaxTxSize), above-including-corner
            // = alloc_[MaxTxSize..2*MaxTxSize], i.e. topLeft = alloc_[MaxTxSize], above[c] =
            // alloc_[MaxTxSize + 1 + c] -- drawing the full 65-byte range every iteration regardless of
            // (w, h) keeps the RNG draw pattern identical to the real test's own for every combination.
            for (int i = 0; i < alloc.Length; i++)
            {
                alloc[i] = rnd.Rand8();
            }

            for (int i = 0; i < h; i++)
            {
                left[i] = alloc[i];
            }

            int topLeft = alloc[MaxTxSize];
            for (int i = 0; i < w; i++)
            {
                above[i] = alloc[MaxTxSize + 1 + i];
            }

            LibaomReferenceFilterIntra.Predict(above, left, topLeft, w, h, mode, expected);

            aboveRow[-1] = topLeft;
            for (int i = 0; i < w; i++)
            {
                aboveRow[i] = above[i];
            }

            for (int i = 0; i < h; i++)
            {
                leftCol[i] = left[i];
            }

            Av1IntraPrediction.Predict(
                pred, w, h, log2W: Log2(w), log2H: Log2(h), aboveRow, leftCol, mode: Av1IntraMode.DcPred,
                haveLeft: true, haveAbove: true, useFilterIntra: true, filterIntraMode: mode, angleDelta: 0,
                enableIntraEdgeFilter: false, filterTypeSmooth: false, maxX: w - 1, maxY: h - 1, x: 0, y: 0, bitDepth: 8);

            for (int i = 0; i < w * h; i++)
            {
                Assert.True(
                    expected[i] == pred[i],
                    $"{modeName} {w}x{h}, iteration {iter}, position {i}: expected {expected[i]}, actual {pred[i]}");
            }
        }
    }

    private static int Log2(int value)
    {
        int result = 0;
        while ((1 << result) < value)
        {
            result++;
        }

        return result;
    }
}
