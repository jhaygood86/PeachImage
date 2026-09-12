using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Chooses and applies AV1's deblocking filter (spec §7.14) for a non-lossless frame, by reusing the
/// decoder's own filter implementation (<see cref="Av1DeblockingFilter"/>) against the encoder's local
/// reconstruction -- the same "no second, driftable copy of real spec logic" approach
/// <see cref="Av1TrialSymbolSink"/> takes for entropy-cost estimation (see <see cref="Av1RdCost"/>'s remarks).
///
/// <para>Deblocking has no direct rate cost to weigh against distortion (spec's <c>loop_filter_level</c> is
/// four fixed 6-bit literals, spec §5.9.11 -- signaling any of the 64 possible values costs exactly the same
/// 12-24 bits regardless of which one is chosen), so unlike <see cref="Av1RdCost"/>'s mode/partition search
/// this is a pure <c>min D</c> search: try each candidate level, filter a scratch copy of the local
/// reconstruction, measure distortion against the true source, keep the best.</para>
///
/// <para><b>Real per-plane/per-direction search (project plan Phase 4)</b>: confirmed directly from libaom
/// source (<c>av1/encoder/picklpf.c</c>'s own real <c>av1_pick_filter_level</c>) that real aomenc, at this
/// project's own tested settings (cpu-used=2, all-intra -- <c>lpf_pick</c> stays at its default
/// <c>LPF_PICK_FROM_FULL_IMAGE</c> there, only escalating to the dual-only variant at speed &gt;= 4), always
/// searches four genuinely independent levels, not one shared value: a first joint pass finds a shared
/// vertical/horizontal baseline, then vertical and horizontal are each independently refined (holding the
/// other at its current value), then U and V are each searched independently (holding both Y levels at their
/// already-finalized values) -- this method now mirrors that same real order and dependency structure. This
/// is a pure search-quality improvement, not a bitstream-correctness one: any of the 64^4 possible level
/// combinations round-trips correctly regardless of how good a choice it is (see the class remarks above),
/// so a real, measured distortion improvement is this change's own real verification, not decode-conformance
/// (which stays trivially satisfied either way).</para>
/// </summary>
internal static class Av1InLoopFilterSearch
{
    // 0 (this encoder's previous always-off behavior) through MaxLoopFilter (63), coarse enough to keep the
    // search cheap (each candidate re-filters and re-measures the whole frame) while still covering AV1's
    // real operating range -- a real encoder's finer-grained/adaptive search is a natural follow-up.
    private static readonly int[] CandidateLevels = [0, 4, 8, 12, 16, 24, 32, 48, 63];

    /// <summary>
    /// Searches <see cref="CandidateLevels"/> for the four independent deblocking levels (Y-vertical,
    /// Y-horizontal, U, V) that minimize squared error against the true source
    /// (<paramref name="sourceY"/>/<paramref name="sourceU"/>/<paramref name="sourceV"/>, already-padded
    /// pre-encode YUV -- comparing against the padded frame rather than cropping to the true unpadded region
    /// is a deliberate simplification: the padding region is a near-flat edge replication (see
    /// <c>Av1FrameEncoder.PadPlane</c>), so it rarely swings the winning level either way), applies the
    /// winning combination in place to <paramref name="reconY"/>/<paramref name="reconU"/>/<paramref name="reconV"/>
    /// (so they reflect the same final pixels a real decoder will reconstruct, matching every other caller's
    /// expectation of those buffers -- see <c>Av1TileEncoder.EncodeTile</c>'s own remarks on that contract),
    /// and returns the four winning levels for <see cref="Av1FrameHeaderWriter.Write"/>'s own
    /// <c>loopFilterLevel0</c>/<c>loopFilterLevel1</c>/<c>loopFilterLevelU</c>/<c>loopFilterLevelV</c>
    /// parameters to actually signal.
    /// </summary>
    public static (int Level0, int Level1, int LevelU, int LevelV) SearchAndApply(
        int[] reconY, int[]? reconU, int[]? reconV,
        int[] sourceY, int[]? sourceU, int[]? sourceV,
        int width, int height, int chromaWidth, int chromaHeight,
        bool monoChrome, int baseQIdx)
    {
        int miCols = 2 * ((width + 7) >> 3);
        int miRows = 2 * ((height + 7) >> 3);
        var seq = BuildSequenceHeader(monoChrome);
        bool hasChroma = reconU is not null;

        // Stage 1: joint baseline -- assume vertical == horizontal, sweep candidates, minimize luma SSE
        // alone (chroma is searched independently in stages 4/5 below, and doesn't affect luma's own
        // filtered output) -- mirrors real picklpf.c's own first `search_filter_level(..., dir=2, ...)` call.
        int joint = SearchOneDimension(seq, reconY, reconU, reconV, sourceY, sourceU, sourceV, width, height, chromaWidth, chromaHeight, monoChrome, baseQIdx, miCols, miRows, level0: -1, level1: -1, levelU: 0, levelV: 0, plane: 0);

        // Stage 2/3: independent vertical/horizontal refinement, each holding the other at its current best
        // -- mirrors real picklpf.c's own two follow-up `search_filter_level(..., dir=0/1, ...)` calls
        // (reachable at this project's own tested cpu-used=2, since LPF_PICK_FROM_FULL_IMAGE_NON_DUAL only
        // applies at speed >= 4).
        int level0 = SearchOneDimension(seq, reconY, reconU, reconV, sourceY, sourceU, sourceV, width, height, chromaWidth, chromaHeight, monoChrome, baseQIdx, miCols, miRows, level0: -1, level1: joint, levelU: 0, levelV: 0, plane: 0);
        int level1 = SearchOneDimension(seq, reconY, reconU, reconV, sourceY, sourceU, sourceV, width, height, chromaWidth, chromaHeight, monoChrome, baseQIdx, miCols, miRows, level0: level0, level1: -1, levelU: 0, levelV: 0, plane: 0);

        // Stage 4/5: U and V, independent of each other and of the now-finalized luma levels -- mirrors
        // real picklpf.c's own `if (num_planes > 1) { filter_level_u = ...; filter_level_v = ...; }` block.
        int levelU = 0, levelV = 0;
        if (hasChroma)
        {
            levelU = SearchOneDimension(seq, reconY, reconU, reconV, sourceY, sourceU, sourceV, width, height, chromaWidth, chromaHeight, monoChrome, baseQIdx, miCols, miRows, level0, level1, levelU: -1, levelV: 0, plane: 1);
            levelV = SearchOneDimension(seq, reconY, reconU, reconV, sourceY, sourceU, sourceV, width, height, chromaWidth, chromaHeight, monoChrome, baseQIdx, miCols, miRows, level0, level1, levelU, levelV: -1, plane: 2);
        }

        // Final apply: filter once more with the fully-decided combination and commit it -- every stage
        // above only measured SSE against scratch clones, never mutating reconY/U/V itself.
        var finalFrame = BuildFrameHeaderForLevels(width, height, monoChrome, baseQIdx, level0, level1, levelU, levelV);
        var finalResult = BuildDecodeResult(seq, finalFrame, reconY, reconU, reconV, miCols, miRows, width, height, chromaWidth, chromaHeight);
        Av1DeblockingFilter.Apply(finalResult);

        return (level0, level1, levelU, levelV);
    }

    /// <summary>
    /// Searches <see cref="CandidateLevels"/> for the single best value of whichever one of
    /// <paramref name="level0"/>/<paramref name="level1"/>/<paramref name="levelU"/>/<paramref name="levelV"/>
    /// is passed as <c>-1</c> (the dimension being searched this call), holding the other three fixed at
    /// their given values, and returns it -- minimizing squared error on <paramref name="plane"/> alone
    /// (0 = Y, 1 = U, 2 = V), matching real picklpf.c's own per-<c>search_filter_level</c>-call plane scope.
    /// </summary>
    private static int SearchOneDimension(
        Av1SequenceHeader seq,
        int[] reconY, int[]? reconU, int[]? reconV,
        int[] sourceY, int[]? sourceU, int[]? sourceV,
        int width, int height, int chromaWidth, int chromaHeight, bool monoChrome, int baseQIdx,
        int miCols, int miRows,
        int level0, int level1, int levelU, int levelV,
        int plane)
    {
        int[]? source = plane switch { 0 => sourceY, 1 => sourceU, _ => sourceV };
        if (source is null)
        {
            return 0;
        }

        int bestLevel = 0;
        long bestSse = long.MaxValue;

        foreach (int candidate in CandidateLevels)
        {
            int l0 = level0 < 0 ? candidate : level0;
            int l1 = level1 < 0 ? candidate : level1;
            int lu = levelU < 0 ? candidate : levelU;
            int lv = levelV < 0 ? candidate : levelV;

            int[] trialY = (int[])reconY.Clone();
            int[]? trialU = (int[]?)reconU?.Clone();
            int[]? trialV = (int[]?)reconV?.Clone();

            var frame = BuildFrameHeaderForLevels(width, height, monoChrome, baseQIdx, l0, l1, lu, lv);
            var result = BuildDecodeResult(seq, frame, trialY, trialU, trialV, miCols, miRows, width, height, chromaWidth, chromaHeight);
            Av1DeblockingFilter.Apply(result);

            int[]? filtered = plane switch { 0 => trialY, 1 => trialU, _ => trialV };
            long sse = ComputeSse(filtered, source);
            if (sse < bestSse)
            {
                bestSse = sse;
                bestLevel = candidate;
            }
        }

        return bestLevel;
    }

    private static long ComputeSse(int[]? filtered, int[]? source)
    {
        if (filtered is null || source is null)
        {
            return 0;
        }

        long sse = 0;
        for (int i = 0; i < filtered.Length; i++)
        {
            int diff = filtered[i] - source[i];
            sse += (long)diff * diff;
        }

        return sse;
    }

    /// <summary>
    /// Builds the same fixed-configuration <see cref="Av1SequenceHeader"/> <see cref="Av1SequenceHeaderWriter.Write"/>
    /// actually encodes -- never serialized itself (this is search-time-only, discarded after the candidate
    /// loop), just the object shape <see cref="Av1DeblockingFilter"/> needs to read. CDEF/restoration/superres
    /// are always off here regardless of what the real sequence header ends up signaling elsewhere, since
    /// this search only ever runs for the deblocking filter.
    /// </summary>
    private static Av1SequenceHeader BuildSequenceHeader(bool monoChrome) => new()
    {
        SeqProfile = Av1SequenceHeaderWriter.SeqProfile,
        SeqLevelIdx0 = Av1SequenceHeaderWriter.SeqLevelIdx0,
        FrameWidthBits = 0,
        FrameHeightBits = 0,
        MaxFrameWidth = 0,
        MaxFrameHeight = 0,
        Use128x128Superblock = false,
        EnableFilterIntra = true,
        EnableIntraEdgeFilter = Av1SequenceHeaderWriter.EnableIntraEdgeFilter,
        EnableSuperres = false,
        EnableCdef = false,
        EnableRestoration = false,
        BitDepth = 8,
        MonoChrome = monoChrome,
        ColorPrimaries = Av1SequenceHeaderWriter.ColorPrimaries,
        TransferCharacteristics = Av1SequenceHeaderWriter.TransferCharacteristics,
        MatrixCoefficients = Av1SequenceHeaderWriter.MatrixCoefficients,
        ColorRange = Av1SequenceHeaderWriter.ColorRangeFull,
        SubsamplingX = true,
        SubsamplingY = true,
        ChromaSamplePosition = Av1SequenceHeaderWriter.ChromaSamplePosition,
        SeparateUvDeltaQ = false,
        FilmGrainParamsPresent = false,
    };

    /// <summary>
    /// Resolves the real <see cref="Av1FrameHeader"/> a candidate four-level combination would produce, by
    /// calling the actual write path (<see cref="Av1FrameHeaderWriter.Write"/>) against a throwaway
    /// <see cref="Av1BitWriter"/> -- reuses that method's already-correct field construction instead of a
    /// second, hand-duplicated copy of it (the same reasoning <see cref="Av1CoefficientWriter.WriteCoeffs"/>'s
    /// <see cref="IAv1SymbolSink"/> reuse follows), at the cost of writing (and discarding) real bits once per
    /// candidate -- cheap next to the filtering/SSE work the same loop iteration already does.
    /// </summary>
    private static Av1FrameHeader BuildFrameHeaderForLevels(int width, int height, bool monoChrome, int baseQIdx, int level0, int level1, int levelU, int levelV)
    {
        var scratchWriter = new Av1BitWriter();
        return Av1FrameHeaderWriter.Write(scratchWriter, width, height, monoChrome, baseQIdx, lossless: false, level0, enableCdef: false, cdef: null, allowScreenContentTools: false, allowIntrabc: false, reducedTxSet: true, level1, levelU, levelV);
    }

    private static Av1FrameDecodeResult BuildDecodeResult(Av1SequenceHeader seq, Av1FrameHeader frame, int[] y, int[]? u, int[]? v, int miCols, int miRows, int width, int height, int chromaWidth, int chromaHeight)
    {
        int frameSize = miCols * miRows;
        int chromaMiCols = miCols / 2;
        int chromaMiRows = miRows / 2;

        var loopfilterTxSizes = new int[3][];
        var loopfilterTxSizeStrides = new int[3];
        loopfilterTxSizes[0] = new int[frameSize];
        Array.Fill(loopfilterTxSizes[0], Av1TxSize.Tx8x8);
        loopfilterTxSizeStrides[0] = miCols;

        if (u is not null)
        {
            int chromaSize = chromaMiCols * chromaMiRows;
            loopfilterTxSizes[1] = new int[chromaSize];
            loopfilterTxSizes[2] = new int[chromaSize];
            Array.Fill(loopfilterTxSizes[1], Av1TxSize.Tx4x4);
            Array.Fill(loopfilterTxSizes[2], Av1TxSize.Tx4x4);
            loopfilterTxSizeStrides[1] = chromaMiCols;
            loopfilterTxSizeStrides[2] = chromaMiCols;
        }
        else
        {
            loopfilterTxSizes[1] = [];
            loopfilterTxSizes[2] = [];
        }

        return new Av1FrameDecodeResult
        {
            Sequence = seq,
            Frame = frame,
            BlocksDecoded = 0,
            TilesStarted = 0,
            Planes = [y, u ?? [], v ?? []],
            PlaneWidths = u is null ? [width, 0, 0] : [width, chromaWidth, chromaWidth],
            PlaneHeights = u is null ? [height, 0, 0] : [height, chromaHeight, chromaHeight],
            StoppedAtResidual = false,
            YModes = [],
            UvModes = [],
            MiSizes = [],
            PaletteSizesY = [],
            PaletteSizesUV = [],
            PaletteColorsYGrid = [],
            PaletteColorsUGrid = [],
            IsInters = [],
            MvRowsGrid = [],
            MvColsGrid = [],
            AngleDeltaYGrid = [],
            AngleDeltaUvGrid = [],
            UseFilterIntraGrid = [],
            FilterIntraModeGrid = [],
            Skips = new bool[frameSize],
            SegmentIds = new int[frameSize],
            DeltaLfs = [new int[frameSize], new int[frameSize], new int[frameSize], new int[frameSize]],
            LoopfilterTxSizes = loopfilterTxSizes,
            LoopfilterTxSizeStrides = loopfilterTxSizeStrides,
            CdefIdx = [],
            RestorationUnits = [null, null, null],
            MinSymbolMaxBitsAtExit = 0,
        };
    }
}
