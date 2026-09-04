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
/// reconstruction, measure distortion against the true source, keep the best. This encoder searches one
/// level shared across all four <c>loop_filter_level</c> slots (Y-vertical, Y-horizontal, U, V) rather than
/// tuning luma/chroma independently -- a v1 simplification; per-plane search is a natural follow-up once this
/// is proven out. CDEF (a further, separate in-loop filter) is not implemented yet -- see the project plan's
/// later Phase 2 sub-step.</para>
/// </summary>
internal static class Av1InLoopFilterSearch
{
    // 0 (this encoder's previous always-off behavior) through MaxLoopFilter (63), coarse enough to keep the
    // search cheap (each candidate re-filters and re-measures the whole frame) while still covering AV1's
    // real operating range -- a real encoder's finer-grained/adaptive search is a natural follow-up.
    private static readonly int[] CandidateLevels = [0, 4, 8, 12, 16, 24, 32, 48, 63];

    /// <summary>
    /// Searches <see cref="CandidateLevels"/> for the deblocking level that minimizes squared error against
    /// the true source (<paramref name="sourceY"/>/<paramref name="sourceU"/>/<paramref name="sourceV"/>,
    /// already-padded pre-encode YUV -- comparing against the padded frame rather than cropping to the true
    /// unpadded region is a deliberate simplification: the padding region is a near-flat edge replication
    /// (see <c>Av1FrameEncoder.PadPlane</c>), so it rarely swings the winning level either way), applies the
    /// winner in place to <paramref name="reconY"/>/<paramref name="reconU"/>/<paramref name="reconV"/> (so
    /// they reflect the same final pixels a real decoder will reconstruct, matching every other caller's
    /// expectation of those buffers -- see <c>Av1TileEncoder.EncodeTile</c>'s own remarks on that contract),
    /// and returns the winning level for <see cref="Av1FrameHeaderWriter.Write"/>'s <c>loopFilterLevel</c>
    /// parameter to actually signal.
    /// </summary>
    public static int SearchAndApply(
        int[] reconY, int[]? reconU, int[]? reconV,
        int[] sourceY, int[]? sourceU, int[]? sourceV,
        int width, int height, int chromaWidth, int chromaHeight,
        bool monoChrome, int baseQIdx)
    {
        int miCols = 2 * ((width + 7) >> 3);
        int miRows = 2 * ((height + 7) >> 3);

        var seq = BuildSequenceHeader(monoChrome);

        int bestLevel = 0;
        long bestSse = ComputeSse(reconY, sourceY) + ComputeSse(reconU, sourceU) + ComputeSse(reconV, sourceV);

        // Best-so-far filtered planes, only allocated once a candidate actually beats level 0 (the common
        // case for already-clean content, where the unfiltered reconstruction is the correct final answer
        // and this search should cost only the ComputeSse calls above, not a single filter pass or clone).
        int[]? bestFilteredY = null;
        int[]? bestFilteredU = null;
        int[]? bestFilteredV = null;

        foreach (int level in CandidateLevels)
        {
            if (level == 0)
            {
                // Already scored above (the unfiltered reconstruction, level 0's actual effect) -- skip
                // re-filtering-and-measuring a no-op.
                continue;
            }

            int[] trialY = (int[])reconY.Clone();
            int[]? trialU = (int[]?)reconU?.Clone();
            int[]? trialV = (int[]?)reconV?.Clone();

            var frame = BuildFrameHeaderForLevel(width, height, monoChrome, baseQIdx, level);
            var result = BuildDecodeResult(seq, frame, trialY, trialU, trialV, miCols, miRows, width, height, chromaWidth, chromaHeight);
            Av1DeblockingFilter.Apply(result);

            long sse = ComputeSse(trialY, sourceY) + ComputeSse(trialU, sourceU) + ComputeSse(trialV, sourceV);
            if (sse < bestSse)
            {
                bestSse = sse;
                bestLevel = level;
                bestFilteredY = trialY;
                bestFilteredU = trialU;
                bestFilteredV = trialV;
            }
        }

        if (bestFilteredY is not null)
        {
            Array.Copy(bestFilteredY, reconY, reconY.Length);
            if (reconU is not null)
            {
                Array.Copy(bestFilteredU!, reconU, reconU.Length);
                Array.Copy(bestFilteredV!, reconV!, reconV!.Length);
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
    /// Resolves the real <see cref="Av1FrameHeader"/> a candidate <paramref name="level"/> would produce, by
    /// calling the actual write path (<see cref="Av1FrameHeaderWriter.Write"/>) against a throwaway
    /// <see cref="Av1BitWriter"/> -- reuses that method's already-correct field construction instead of a
    /// second, hand-duplicated copy of it (the same reasoning <see cref="Av1CoefficientWriter.WriteCoeffs"/>'s
    /// <see cref="IAv1SymbolSink"/> reuse follows), at the cost of writing (and discarding) real bits once per
    /// candidate -- cheap next to the filtering/SSE work the same loop iteration already does.
    /// </summary>
    private static Av1FrameHeader BuildFrameHeaderForLevel(int width, int height, bool monoChrome, int baseQIdx, int level)
    {
        var scratchWriter = new Av1BitWriter();
        return Av1FrameHeaderWriter.Write(scratchWriter, width, height, monoChrome, baseQIdx, lossless: false, loopFilterLevel: level);
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
            MiSizes = [],
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
