namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// libaom's winner-mode re-evaluation tiers (<c>MULTI_WINNER_MODE_TYPE</c>, <c>av1/encoder/speed_features.h</c>),
/// how many of the cheap first pass's top candidates get a second, more expensive re-check.
/// </summary>
internal enum Av1WinnerModeType
{
    Off = 0,
    Fast = 1,
    Default = 2,
}

/// <summary>
/// AV1 ALL_INTRA-mode encoder speed-feature levels, 0 (slowest/most thorough) through 9 (fastest/most
/// pruned) -- a faithful field-by-field port of libaom's own
/// <c>set_allintra_speed_features_framesize_independent</c> (<c>av1/encoder/speed_features.c:345-616</c>),
/// the function libaom uses to configure a <c>--cpu-used</c> value under AV1's all-intra usage mode (the
/// mode AVIF still-image encoding uses -- <c>aomenc --help</c> confirms cpu-used 0-9 is only valid range
/// under <c>--usage=allintra</c>, a wider range than GOOD mode's 0-6). See
/// <see cref="AvifEncoderOptions.Effort"/> for how a caller selects a level.
///
/// <para>Deliberately not a per-level switch: like libaom's own function, <see cref="Compute"/> is a single
/// ordered cascade of <c>if (effort >= N)</c> assignments (each level a superset of the previous one's
/// changes), so it stays trivially diffable against the libaom source it mirrors when re-verifying a
/// specific field's value. Two fields are genuine, deliberate exceptions to that monotonicity, called out at
/// their assignment sites below: <see cref="ChromaIntraPruningWithHog"/> (active only at exactly effort 3,
/// then unconditionally forced back off) and <see cref="MultiWinnerModeType"/> (candidate count sequence
/// 1,1,1,1,3,2,1,1,1,1 for effort 0-9) -- both are libaom's own behavior, not simplification errors, and must
/// not be "smoothed" into a monotonic curve when consuming this type.</para>
/// </summary>
internal sealed record Av1SpeedFeatures(
    int TopIntraModelCountAllowed,
    int IntraPruningWithHog,
    int ChromaIntraPruningWithHog,
    int PruneFilterIntraLevel,
    bool DisableSmoothIntra,
    bool PruneSmoothIntraModeForChroma,
    bool PruneChromaModesUsingLumaWinner,
    int CflSearchRange,
    bool AdaptTopModelRdCountUsingNeighbors,
    bool PruneLumaOddDeltaAnglesInIntra,
    int PrunePaletteSearchLevel,
    int PruneLumaPaletteSizeSearchLevel,
    bool EarlyTermChromaPaletteSizeSearch,
    int ColorPaletteThresh,
    bool UseIntrabc,
    bool PruneIntrabcCandidateBlockHashSearch,
    bool HashMax8x8IntrabcBlocks,
    int IntrabcSearchLevel,
    int SimpleMotionSearchPruneAgg,
    int ExtPartitionEvalThreshBlockPixels,
    int SimpleMotionSearchSplit,
    int IntraCnnBasedPartPruneLevel,
    int Ml4PartitionSearchLevelIndex,
    Av1WinnerModeType MultiWinnerModeType,
    bool EnableWinnerModeForCoeffOpt,
    bool EnableWinnerModeForUseTxDomainDist,
    bool EnableWinnerModeForTxSizeSrch,
    int PruneWinnerModeEvalLevel,
    int DcBlkPredLevel)
{
    /// <summary>libaom's <c>NO_PRUNING</c> sentinel for <see cref="SimpleMotionSearchPruneAgg"/> (aggressiveness levels otherwise run 0-5).</summary>
    public const int NoPruning = -1;

    /// <summary>
    /// libaom's <c>winner_mode_count_allowed[]</c> (<c>av1/encoder/rdopt_utils.h:236-239</c>): how many of the
    /// cheap first pass's top candidates get a second, more expensive re-evaluation.
    /// </summary>
    public int WinnerModeCandidateCount => MultiWinnerModeType switch
    {
        Av1WinnerModeType.Fast => 2,
        Av1WinnerModeType.Default => 3,
        _ => 1,
    };

    /// <summary>
    /// Computes the speed-feature set for <paramref name="effort"/> (libaom's <c>--cpu-used</c>, 0-9 under
    /// ALL_INTRA), given whether this frame has screen-content tools enabled (libaom's
    /// <c>allow_screen_content_tools</c> -- several partition-pruning thresholds branch on this).
    /// </summary>
    public static Av1SpeedFeatures Compute(int effort, bool allowScreenContentTools)
    {
        if (effort is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(effort), effort, "AV1 ALL_INTRA cpu-used is only defined for 0-9.");
        }

        // Effort 0 baseline: init_intra_sf/init_mv_sf/init_part_sf/init_winner_mode_sf's defaults, plus the
        // handful of assignments libaom applies unconditionally before any `if (speed >= N)` gate
        // (speed_features.c:352-384) -- i.e. exactly what effort 0 alone produces.
        int topIntraModelCountAllowed = 4;
        int intraPruningWithHog = 1;
        int chromaIntraPruningWithHog = 0;
        int pruneFilterIntraLevel = 0;
        bool disableSmoothIntra = false;
        bool pruneSmoothIntraModeForChroma = false;
        bool pruneChromaModesUsingLumaWinner = false;
        int cflSearchRange = 3;
        bool adaptTopModelRdCountUsingNeighbors = false;
        bool pruneLumaOddDeltaAnglesInIntra = false;
        int prunePaletteSearchLevel = 0;
        int pruneLumaPaletteSizeSearchLevel = 1;
        bool pruneIntrabcCandidateBlockHashSearch = false;
        bool hashMax8x8IntrabcBlocks = false;
        int intrabcSearchLevel = 0;
        int simpleMotionSearchPruneAgg = 0; // SIMPLE_AGG_LVL0
        int extPartitionEvalThreshBlockPixels = 8; // BLOCK_8X8
        int simpleMotionSearchSplit = 0;
        int intraCnnBasedPartPruneLevel = 0;

        // Ml4PartitionSearchLevelIndex is the one field this port pulls in from libaom's own separate
        // set_allintra_speed_feature_framesize_dependent (speed_features.c:166-344, not otherwise ported
        // here -- see this record's own remarks) rather than framesize_independent: real libaom's own
        // cascade for this specific field (speed_features.c:209-271) never branches on resolution, only on
        // speed, so it's safe to fold into this otherwise-framesize-independent cascade without actually
        // needing a width/height parameter.
        int ml4PartitionSearchLevelIndex = 0;
        var multiWinnerModeType = Av1WinnerModeType.Off;
        bool enableWinnerModeForCoeffOpt = false;
        bool enableWinnerModeForUseTxDomainDist = false;
        bool enableWinnerModeForTxSizeSrch = false;
        int pruneWinnerModeEvalLevel = 0;
        int dcBlkPredLevel = 0;

        if (effort >= 1)
        {
            topIntraModelCountAllowed = 3;
            prunePaletteSearchLevel = 1;
            pruneLumaPaletteSizeSearchLevel = 2;
            pruneIntrabcCandidateBlockHashSearch = true;
            simpleMotionSearchPruneAgg = allowScreenContentTools ? NoPruning : 1; // SIMPLE_AGG_LVL1
            simpleMotionSearchSplit = allowScreenContentTools ? 1 : 2;
            intraCnnBasedPartPruneLevel = allowScreenContentTools ? 0 : 2;
            ml4PartitionSearchLevelIndex = 1;
        }

        if (effort >= 2)
        {
            intraPruningWithHog = 2;
            pruneFilterIntraLevel = 1;
            disableSmoothIntra = true;
            simpleMotionSearchPruneAgg = allowScreenContentTools ? NoPruning : 2; // SIMPLE_AGG_LVL2
            ml4PartitionSearchLevelIndex = 2;
        }

        if (effort >= 3)
        {
            intraPruningWithHog = 3;
            chromaIntraPruningWithHog = 2;
            prunePaletteSearchLevel = 2;
            simpleMotionSearchPruneAgg = 3; // SIMPLE_AGG_LVL3, unconditional (screen-content or not) from here on
            ml4PartitionSearchLevelIndex = 3;
        }

        if (effort >= 4)
        {
            pruneChromaModesUsingLumaWinner = true;
            simpleMotionSearchPruneAgg = 4; // SIMPLE_AGG_LVL4
            hashMax8x8IntrabcBlocks = true;
            multiWinnerModeType = Av1WinnerModeType.Default;
            enableWinnerModeForCoeffOpt = true;
            enableWinnerModeForUseTxDomainDist = true;
            enableWinnerModeForTxSizeSrch = true;
        }

        if (effort >= 5)
        {
            simpleMotionSearchPruneAgg = 5; // SIMPLE_AGG_LVL5
            extPartitionEvalThreshBlockPixels = allowScreenContentTools ? 8 : 16; // BLOCK_16X16
            intraCnnBasedPartPruneLevel = allowScreenContentTools ? 1 : 2;
            multiWinnerModeType = Av1WinnerModeType.Fast;
        }

        if (effort >= 6)
        {
            topIntraModelCountAllowed = 2;
            intraPruningWithHog = 4;
            pruneFilterIntraLevel = 2;
            pruneSmoothIntraModeForChroma = true;
            cflSearchRange = 1;
            adaptTopModelRdCountUsingNeighbors = true;
            pruneLumaOddDeltaAnglesInIntra = true;
            intrabcSearchLevel = 1;
            multiWinnerModeType = Av1WinnerModeType.Off;
            pruneWinnerModeEvalLevel = 1;
            dcBlkPredLevel = 1;
        }

        // Effort 7-9: no further libaom assignments beyond effort 6 for ALL_INTRA.

        // Unconditional post-fix (speed_features.c:614-615): chroma HOG pruning is only ever active at
        // exactly effort 3 -- forced back off from effort 4 onward because pruneChromaModesUsingLumaWinner
        // takes over from there. This is libaom's own behavior, not a bug to smooth away.
        if (pruneChromaModesUsingLumaWinner)
        {
            chromaIntraPruningWithHog = 0;
        }

        return new Av1SpeedFeatures(
            topIntraModelCountAllowed,
            intraPruningWithHog,
            chromaIntraPruningWithHog,
            pruneFilterIntraLevel,
            disableSmoothIntra,
            pruneSmoothIntraModeForChroma,
            pruneChromaModesUsingLumaWinner,
            cflSearchRange,
            adaptTopModelRdCountUsingNeighbors,
            pruneLumaOddDeltaAnglesInIntra,
            prunePaletteSearchLevel,
            pruneLumaPaletteSizeSearchLevel,
            EarlyTermChromaPaletteSizeSearch: true, // Unconditional base assignment (speed_features.c:364), constant at every effort.
            ColorPaletteThresh: 64, // encodeframe.c:1305, constant at every effort for ALL_INTRA (only nonrd/real-time paths -- unused here -- vary it).
            UseIntrabc: true, // Never touched by the ALL_INTRA table (unlike GOOD mode); IntraBC search always structurally available here.
            pruneIntrabcCandidateBlockHashSearch,
            hashMax8x8IntrabcBlocks,
            intrabcSearchLevel,
            simpleMotionSearchPruneAgg,
            extPartitionEvalThreshBlockPixels,
            simpleMotionSearchSplit,
            intraCnnBasedPartPruneLevel,
            ml4PartitionSearchLevelIndex,
            multiWinnerModeType,
            enableWinnerModeForCoeffOpt,
            enableWinnerModeForUseTxDomainDist,
            enableWinnerModeForTxSizeSrch,
            pruneWinnerModeEvalLevel,
            dcBlkPredLevel);
    }
}
