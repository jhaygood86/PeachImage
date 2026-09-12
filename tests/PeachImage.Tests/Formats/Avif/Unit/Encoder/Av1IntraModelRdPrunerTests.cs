using PeachImage.Formats.Avif.Encoder.Av1;
using PeachImage.Tests.Formats.Avif.Unit;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1IntraModelRdPruner"/>'s port of libaom's real <c>aom_hadamard_4x4</c>/<c>aom_satd</c>
/// and <c>prune_intra_y_mode</c> against hand-computed expectations.
///
/// <para><b>CompareReferenceRandom_MatchesLibaomReference</b>/<b>CompareReferenceExtreme_MatchesLibaomReference</b>
/// (project plan's own libaom test-port item): a faithful port of libaom's own <c>hadamard_test.cc</c>
/// <c>HadamardLowbdTest.CompareReferenceRandom</c>/<c>CompareReferenceExtreme</c>, using
/// <see cref="LibaomAcmRandom"/> (the same deterministic PRNG port used for the WHT test port) and
/// <see cref="LibaomReferenceHadamard"/> (transcribed directly from <c>aom_dsp/avg.c</c>'s own
/// <c>aom_hadamard_4x4_c</c>/<c>hadamard_col4</c>/<c>aom_satd_c</c>, independent of
/// <see cref="Av1IntraModelRdPruner"/>'s own implementation) as the reference to compare against --
/// unlike the hand-traced tests above, this proves the port matches libaom's own real transform for a
/// large, deterministic sweep of inputs, not just one manually-verified case.</para>
/// </summary>
public class Av1IntraModelRdPrunerTests
{
    [Fact]
    public void Hadamard4x4_AllZeroResidual_ProducesAllZeroCoefficients()
    {
        int[] residual = new int[16];
        Span<int> coeff = stackalloc int[16];

        Av1IntraModelRdPruner.Hadamard4x4(residual, 4, coeff);

        foreach (int c in coeff)
        {
            Assert.Equal(0, c);
        }

        Assert.Equal(0, Av1IntraModelRdPruner.Satd(coeff));
    }

    [Fact]
    public void Hadamard4x4_SequentialResidual_MatchesHandTracedCoefficients()
    {
        // 4x4 residual [1..16] row-major, stride 4. Hand-traced through aom_hadamard_4x4_c's own two-pass
        // column/cross-column butterfly plus its final transpose (column pass per column: (1,5,9,13) ->
        // (14,-4,-8,0), (2,6,10,14) -> (16,-4,-8,0), (3,7,11,15) -> (18,-4,-8,0), (4,8,12,16) -> (20,-4,-8,0);
        // cross-column pass over the intermediate buffer's own k=0..3 rows: (14,16,18,20) -> (34,-2,-4,0),
        // (-4,-4,-4,-4) -> (-8,0,0,0), (-8,-8,-8,-8) -> (-16,0,0,0), (0,0,0,0) -> (0,0,0,0); then transposed
        // into row-major order) -- an independent derivation from the port itself, not a self-consistency
        // check.
        int[] residual = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        Span<int> coeff = stackalloc int[16];

        Av1IntraModelRdPruner.Hadamard4x4(residual, 4, coeff);

        int[] expected = [34, -8, -16, 0, -2, 0, 0, 0, -4, 0, 0, 0, 0, 0, 0, 0];
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(expected[i], coeff[i]);
        }

        Assert.Equal(64, Av1IntraModelRdPruner.Satd(coeff));
    }

    [Fact]
    public void Satd_NegativeCoefficients_SumsAbsoluteValues()
    {
        int[] coeff = [-5, 3, -2, 0];
        Assert.Equal(10, Av1IntraModelRdPruner.Satd(coeff));
    }

    [Fact]
    public void PruneIntraYMode_MatchesHandTracedSequence()
    {
        // Hand-traced against prune_intra_y_mode's own real insertion/threshold logic (thresh_top = 1.00,
        // thresh_best = 1.50) for a count-3 top-K list: candidates 100, 90, 95 fill and refine the top-3
        // (none pruned, since fewer than 3 real candidates exist yet or they rank within the top 3 outright);
        // 200 and 120 are both worse than the (now full) 3rd-best (100) by more than thresh_top, so both are
        // pruned; 99 ties into the 3rd-best slot exactly (thresh_top's `>` is strict) and survives.
        long bestModelRd = long.MaxValue;
        Span<long> topModelRd = stackalloc long[3];
        topModelRd.Fill(long.MaxValue);

        Assert.False(Av1IntraModelRdPruner.PruneIntraYMode(100, ref bestModelRd, topModelRd));
        Assert.False(Av1IntraModelRdPruner.PruneIntraYMode(90, ref bestModelRd, topModelRd));
        Assert.False(Av1IntraModelRdPruner.PruneIntraYMode(95, ref bestModelRd, topModelRd));
        Assert.True(Av1IntraModelRdPruner.PruneIntraYMode(200, ref bestModelRd, topModelRd));
        Assert.True(Av1IntraModelRdPruner.PruneIntraYMode(120, ref bestModelRd, topModelRd));
        Assert.False(Av1IntraModelRdPruner.PruneIntraYMode(99, ref bestModelRd, topModelRd));

        Assert.Equal(90, bestModelRd);
    }

    [Fact]
    public void PruneIntraYMode_FirstCandidate_NeverPruned()
    {
        long bestModelRd = long.MaxValue;
        Span<long> topModelRd = stackalloc long[4];
        topModelRd.Fill(long.MaxValue);

        Assert.False(Av1IntraModelRdPruner.PruneIntraYMode(1_000_000, ref bestModelRd, topModelRd));
        Assert.Equal(1_000_000, bestModelRd);
    }

    /// <summary>
    /// Port of libaom's own <c>hadamard_test.cc</c> <c>HadamardLowbdTest.CompareReferenceRandom</c>: 1,000
    /// 4x4 blocks of the same deterministic residual data (<c>src - pred</c>, each an independent
    /// <c>Rand8()</c> draw, matching <c>HadamardLowbdTest::Rand</c> exactly) libaom's own real test feeds
    /// <c>aom_hadamard_4x4_c</c>, asserting <see cref="Av1IntraModelRdPruner.Hadamard4x4"/> is bit-exact
    /// against <see cref="LibaomReferenceHadamard.Hadamard4x4"/> -- libaom's own real algorithm, independently
    /// transcribed, not a self-consistency check against the port's own hand-traced expectations above.
    /// libaom's own test sorts both outputs before comparing (order doesn't matter to its own real caller,
    /// <c>av1_quick_txfm</c>/<c>aom_satd</c>), but this port's own row-major output contract is a real,
    /// meaningful thing to hold bit-exact position-for-position, so this test compares unsorted -- a
    /// strictly stronger check.
    /// </summary>
    [Fact]
    public void CompareReferenceRandom_MatchesLibaomReference()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        const int countTestBlock = 1000;

        for (int i = 0; i < countTestBlock; i++)
        {
            var a = new short[16];
            for (int j = 0; j < 16; j++)
            {
                short src = rnd.Rand8();
                short pred = rnd.Rand8();
                a[j] = (short)(src - pred);
            }

            var expected = new int[16];
            LibaomReferenceHadamard.Hadamard4x4(a, 4, expected);

            var residual = new int[16];
            for (int j = 0; j < 16; j++)
            {
                residual[j] = a[j];
            }

            var actual = new int[16];
            Av1IntraModelRdPruner.Hadamard4x4(residual, 4, actual);

            for (int j = 0; j < 16; j++)
            {
                Assert.Equal(expected[j], actual[j]);
            }

            Assert.Equal(LibaomReferenceHadamard.Satd(expected), Av1IntraModelRdPruner.Satd(actual));
        }
    }

    /// <summary>
    /// Port of libaom's own <c>hadamard_test.cc</c> <c>HadamardTestBase.CompareReferenceExtreme</c>: every
    /// sample pinned to +/-255 (the real 8-bit residual extreme, <c>(1 &lt;&lt; kBitDepth) - 1</c>), the case
    /// most likely to expose an <see langword="int"/> vs. libaom's own real <c>int16_t</c> intermediate
    /// truncation mismatch -- see <see cref="LibaomReferenceHadamard"/>'s own remarks.
    /// </summary>
    [Theory]
    [InlineData(255)]
    [InlineData(-255)]
    public void CompareReferenceExtreme_MatchesLibaomReference(int extreme)
    {
        var a = new short[16];
        Array.Fill(a, (short)extreme);

        var expected = new int[16];
        LibaomReferenceHadamard.Hadamard4x4(a, 4, expected);

        var residual = new int[16];
        for (int j = 0; j < 16; j++)
        {
            residual[j] = a[j];
        }

        Span<int> actual = stackalloc int[16];
        Av1IntraModelRdPruner.Hadamard4x4(residual, 4, actual);

        for (int j = 0; j < 16; j++)
        {
            Assert.Equal(expected[j], actual[j]);
        }

        Assert.Equal(LibaomReferenceHadamard.Satd(expected), Av1IntraModelRdPruner.Satd(actual));
    }
}
