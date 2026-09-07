using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1IntraModelRdPruner"/>'s port of libaom's real <c>aom_hadamard_4x4</c>/<c>aom_satd</c>
/// and <c>prune_intra_y_mode</c> against hand-computed expectations.
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
}
