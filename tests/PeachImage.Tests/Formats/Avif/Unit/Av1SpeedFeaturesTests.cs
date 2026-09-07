using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Verifies <see cref="Av1SpeedFeatures.Compute"/> against the researched libaom
/// <c>set_allintra_speed_features_framesize_independent</c> cascade table (see the project plan
/// "compare-avif-encoding-to-lucky-clover.md" §0) field by field, at every effort level 0-9, for both
/// screen-content and non-screen-content frames -- including the two deliberately non-monotonic wrinkles
/// (chroma HOG pruning only active at effort 3; winner-mode candidate count sequence 1,1,1,1,3,2,1,1,1,1).
/// </summary>
public class Av1SpeedFeaturesTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void Compute_RejectsOutOfRangeEffort(int effort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Av1SpeedFeatures.Compute(effort, allowScreenContentTools: true));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 3)]
    [InlineData(5, 3)]
    [InlineData(6, 2)]
    [InlineData(9, 2)]
    public void TopIntraModelCountAllowed_MatchesTable(int effort, int expected)
    {
        Assert.Equal(expected, Av1SpeedFeatures.Compute(effort, allowScreenContentTools: false).TopIntraModelCountAllowed);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(5, 3)]
    [InlineData(6, 4)]
    [InlineData(9, 4)]
    public void IntraPruningWithHog_MatchesTable(int effort, int expected)
    {
        Assert.Equal(expected, Av1SpeedFeatures.Compute(effort, allowScreenContentTools: false).IntraPruningWithHog);
    }

    /// <summary>Chroma HOG pruning is only ever active at exactly effort 3 -- forced back off from effort 4 onward once <c>PruneChromaModesUsingLumaWinner</c> takes over (speed_features.c:614-615). Not monotonic; must not be "smoothed".</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 2)]
    [InlineData(4, 0)]
    [InlineData(9, 0)]
    public void ChromaIntraPruningWithHog_OnlyActiveAtExactlyEffort3(int effort, int expected)
    {
        Assert.Equal(expected, Av1SpeedFeatures.Compute(effort, allowScreenContentTools: false).ChromaIntraPruningWithHog);
    }

    [Fact]
    public void PruneChromaModesUsingLumaWinner_TrueFromEffort4Onward()
    {
        Assert.False(Av1SpeedFeatures.Compute(3, allowScreenContentTools: false).PruneChromaModesUsingLumaWinner);
        Assert.True(Av1SpeedFeatures.Compute(4, allowScreenContentTools: false).PruneChromaModesUsingLumaWinner);
        Assert.True(Av1SpeedFeatures.Compute(9, allowScreenContentTools: false).PruneChromaModesUsingLumaWinner);
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, false, 0)]
    [InlineData(2, false, 1)]
    [InlineData(5, false, 1)]
    [InlineData(6, false, 2)]
    public void PruneFilterIntraLevel_MatchesTable(int effort, bool screenContent, int expected)
    {
        Assert.Equal(expected, Av1SpeedFeatures.Compute(effort, screenContent).PruneFilterIntraLevel);
    }

    [Fact]
    public void DisableSmoothIntra_TrueFromEffort2Onward()
    {
        Assert.False(Av1SpeedFeatures.Compute(1, allowScreenContentTools: false).DisableSmoothIntra);
        Assert.True(Av1SpeedFeatures.Compute(2, allowScreenContentTools: false).DisableSmoothIntra);
        Assert.True(Av1SpeedFeatures.Compute(9, allowScreenContentTools: false).DisableSmoothIntra);
    }

    [Fact]
    public void Effort6Fields_AllTurnOnTogether()
    {
        var sf5 = Av1SpeedFeatures.Compute(5, allowScreenContentTools: false);
        var sf6 = Av1SpeedFeatures.Compute(6, allowScreenContentTools: false);

        Assert.False(sf5.PruneSmoothIntraModeForChroma);
        Assert.True(sf6.PruneSmoothIntraModeForChroma);

        Assert.Equal(3, sf5.CflSearchRange);
        Assert.Equal(1, sf6.CflSearchRange);

        Assert.False(sf5.AdaptTopModelRdCountUsingNeighbors);
        Assert.True(sf6.AdaptTopModelRdCountUsingNeighbors);

        Assert.False(sf5.PruneLumaOddDeltaAnglesInIntra);
        Assert.True(sf6.PruneLumaOddDeltaAnglesInIntra);

        Assert.Equal(0, sf5.IntrabcSearchLevel);
        Assert.Equal(1, sf6.IntrabcSearchLevel);

        Assert.Equal(0, sf5.PruneWinnerModeEvalLevel);
        Assert.Equal(1, sf6.PruneWinnerModeEvalLevel);

        Assert.Equal(0, sf5.DcBlkPredLevel);
        Assert.Equal(1, sf6.DcBlkPredLevel);
    }

    [Fact]
    public void PalettePruneLevels_MatchTable()
    {
        // Table: PrunePaletteSearchLevel 0 (effort 0) -> 1 (effort>=1) -> 2 (effort>=3).
        Assert.Equal(0, Av1SpeedFeatures.Compute(0, allowScreenContentTools: false).PrunePaletteSearchLevel);
        Assert.Equal(1, Av1SpeedFeatures.Compute(1, allowScreenContentTools: false).PrunePaletteSearchLevel);
        Assert.Equal(1, Av1SpeedFeatures.Compute(2, allowScreenContentTools: false).PrunePaletteSearchLevel);
        Assert.Equal(2, Av1SpeedFeatures.Compute(3, allowScreenContentTools: false).PrunePaletteSearchLevel);
        Assert.Equal(2, Av1SpeedFeatures.Compute(9, allowScreenContentTools: false).PrunePaletteSearchLevel);

        // PruneLumaPaletteSizeSearchLevel: 1 (effort 0, unconditional base) -> 2 (effort>=1), constant after.
        Assert.Equal(1, Av1SpeedFeatures.Compute(0, allowScreenContentTools: false).PruneLumaPaletteSizeSearchLevel);
        Assert.Equal(2, Av1SpeedFeatures.Compute(1, allowScreenContentTools: false).PruneLumaPaletteSizeSearchLevel);
        Assert.Equal(2, Av1SpeedFeatures.Compute(9, allowScreenContentTools: false).PruneLumaPaletteSizeSearchLevel);
    }

    [Fact]
    public void EarlyTermChromaPaletteSizeSearchAndColorPaletteThresh_ConstantAtEveryEffort()
    {
        for (int effort = 0; effort <= 9; effort++)
        {
            var sf = Av1SpeedFeatures.Compute(effort, allowScreenContentTools: effort % 2 == 0);
            Assert.True(sf.EarlyTermChromaPaletteSizeSearch);
            Assert.Equal(64, sf.ColorPaletteThresh);
            Assert.True(sf.UseIntrabc);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(9, true)]
    public void PruneIntrabcCandidateBlockHashSearch_TrueFromEffort1Onward(int effort, bool expected)
    {
        Assert.Equal(expected, Av1SpeedFeatures.Compute(effort, allowScreenContentTools: false).PruneIntrabcCandidateBlockHashSearch);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(9, true)]
    public void HashMax8x8IntrabcBlocks_TrueFromEffort4Onward(int effort, bool expected)
    {
        Assert.Equal(expected, Av1SpeedFeatures.Compute(effort, allowScreenContentTools: false).HashMax8x8IntrabcBlocks);
    }

    [Fact]
    public void SimpleMotionSearchPruneAgg_ScreenContentStaysUnprunedUntilEffort3ThenMatchesNonScreenContent()
    {
        Assert.Equal(0, Av1SpeedFeatures.Compute(0, allowScreenContentTools: true).SimpleMotionSearchPruneAgg);
        Assert.Equal(Av1SpeedFeatures.NoPruning, Av1SpeedFeatures.Compute(1, allowScreenContentTools: true).SimpleMotionSearchPruneAgg);
        Assert.Equal(Av1SpeedFeatures.NoPruning, Av1SpeedFeatures.Compute(2, allowScreenContentTools: true).SimpleMotionSearchPruneAgg);

        // From effort 3 onward, pruning becomes unconditional regardless of screen-content-tools.
        for (int effort = 3; effort <= 9; effort++)
        {
            int expectedLevel = Math.Min(effort, 5);
            Assert.Equal(expectedLevel, Av1SpeedFeatures.Compute(effort, allowScreenContentTools: true).SimpleMotionSearchPruneAgg);
            Assert.Equal(expectedLevel, Av1SpeedFeatures.Compute(effort, allowScreenContentTools: false).SimpleMotionSearchPruneAgg);
        }
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void ExtPartitionEvalThreshBlockPixels_SplitsByScreenContentFromEffort5(int effort, bool changes)
    {
        var screen = Av1SpeedFeatures.Compute(effort, allowScreenContentTools: true);
        var nonScreen = Av1SpeedFeatures.Compute(effort, allowScreenContentTools: false);

        Assert.Equal(8, screen.ExtPartitionEvalThreshBlockPixels);
        Assert.Equal(changes ? 16 : 8, nonScreen.ExtPartitionEvalThreshBlockPixels);
    }

    /// <summary>Deliberately non-monotonic: candidate count kept for winner-mode re-evaluation is 1,1,1,1,3,2,1,1,1,1 for effort 0-9 (speed_features.c). A "cleaned up" monotonic curve here would be wrong.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 3)]
    [InlineData(5, 2)]
    [InlineData(6, 1)]
    [InlineData(9, 1)]
    public void WinnerModeCandidateCount_MatchesNonMonotonicSequence(int effort, int expectedCount)
    {
        Assert.Equal(expectedCount, Av1SpeedFeatures.Compute(effort, allowScreenContentTools: false).WinnerModeCandidateCount);
    }

    [Fact]
    public void EnableWinnerModeFlags_TurnOnAtEffort4()
    {
        var sf3 = Av1SpeedFeatures.Compute(3, allowScreenContentTools: false);
        var sf4 = Av1SpeedFeatures.Compute(4, allowScreenContentTools: false);

        Assert.False(sf3.EnableWinnerModeForCoeffOpt);
        Assert.False(sf3.EnableWinnerModeForUseTxDomainDist);
        Assert.False(sf3.EnableWinnerModeForTxSizeSrch);

        Assert.True(sf4.EnableWinnerModeForCoeffOpt);
        Assert.True(sf4.EnableWinnerModeForUseTxDomainDist);
        Assert.True(sf4.EnableWinnerModeForTxSizeSrch);
    }

    [Fact]
    public void HigherEffort_NeverExceeds9()
    {
        var sf9 = Av1SpeedFeatures.Compute(9, allowScreenContentTools: false);
        Assert.Equal(2, sf9.TopIntraModelCountAllowed);
        Assert.Equal(4, sf9.IntraPruningWithHog);
        Assert.Equal(1, sf9.IntrabcSearchLevel);
    }
}
