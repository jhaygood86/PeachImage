using System.Buffers;
using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Chooses and applies AV1's CDEF filter (spec §7.15) for a non-lossless frame, by reusing the decoder's own
/// filter implementation (<see cref="Av1Cdef"/>) against the encoder's (already deblocked -- see
/// <see cref="Av1InLoopFilterSearch"/>, which must run first per spec's own deblock-then-CDEF ordering) local
/// reconstruction -- the same "no second, driftable copy of real spec logic" approach this encoder already
/// takes for entropy costing (<see cref="Av1TrialSymbolSink"/>) and deblocking.
///
/// <para><b>Stage 2: real per-64x64-unit adaptive CDEF</b> (spec's up to <c>CDEF_MAX_STRENGTHS</c> = 8 strength
/// combos, each unit independently choosing which one to use via a signalled <c>cdef_idx</c>). Real libaom's
/// own algorithm (<c>av1_cdef_search</c>, <c>av1/encoder/pickcdef.c</c>) has three parts, all ported here:
/// <list type="number">
/// <item><description>Per-unit candidate MSE table: for every 64x64 unit and every candidate strength combo,
/// filter that unit and compute pure SSE against the true source.</description></item>
/// <item><description>Greedy clustering (<c>search_one</c>/<c>joint_strength_search</c>) to decide
/// <c>cdef_bits</c> (how many of up to 8 strength slots are actually kept, frame-wide): for each candidate
/// bit-count, greedily pick whichever remaining candidate strength minimizes the sum over all units of
/// <c>min(best-so-far, this candidate's own MSE for that unit)</c> -- classic greedy facility-location/
/// k-medoid selection, not an exhaustive search. A real Lagrangian RD cost (<see cref="Av1RdCost"/>, this
/// project's own qindex-derived lambda) picks the winning bit-count -- this is the only place rate enters;
/// the greedy selection and the final per-unit remap are pure min-MSE.</description></item>
/// <item><description>Final per-unit remap: once the kept combo set is fixed, each unit's own <c>cdef_idx</c>
/// is just "whichever kept combo minimizes this unit's own MSE."</description></item>
/// </list></para>
///
/// <para><b>Disclosed scope narrowing vs. real libaom</b>, matching this project's own "the pruning/heuristic
/// is real, the scope is disclosed" convention elsewhere: (1) <see cref="Candidates"/> shares one (pri, sec)
/// strength between luma and chroma per combo rather than real libaom's separate luma/chroma plane-group
/// search -- the same simplification the v1 frame-wide search already made, now applied per-unit instead of
/// per-frame (a real improvement either way, since v1 could only ever pick one combo for the whole frame).
/// (2) The per-unit skip rule (a 64x64 unit is exempt from CDEF, and therefore never needs its own MSE
/// evaluated or its own <c>cdef_idx</c> literal written, whenever every mode-info block inside it has
/// <c>skip_txfm == 1</c>) is not applied: this project's own non-lossless leaves never set
/// <c>TileState.Skips</c> at all today (see <see cref="Av1TileEncoder.EncodeLeaf"/>'s own remarks), so
/// treating every unit as non-skip is not an approximation of the real rule, it's exactly what that rule
/// evaluates to given this encoder's own current, disclosed skip semantics -- revisit if that ever changes.
/// Damping is likewise still fixed at 3, not yet the real <c>3 + (base_qindex &gt;&gt; 6)</c> formula -- a
/// separate, still-open item, not addressed by this round.</para>
///
/// <para><b>Buffer ownership</b>: <see cref="Av1Cdef.Apply"/> always replaces every <c>Planes</c> element with
/// a freshly <see cref="ArrayPool{T}.Shared"/>-rented buffer and returns whatever was passed in to the same
/// pool -- including a plain <c>new int[]</c> array, which throws (<c>ArgumentException</c>, "not associated
/// with this pool") rather than being silently accepted. Every buffer this type feeds to <see cref="Av1Cdef.Apply"/>
/// is therefore explicitly pool-rented first (<see cref="RentAndCopy"/>), and every array <see cref="Av1Cdef.Apply"/>
/// hands back is explicitly returned (<see cref="ReturnPlanes"/>) once this type is done reading it --
/// <see cref="SearchAndApply"/>'s own <c>reconY</c>/<c>reconU</c>/<c>reconV</c> parameters are never fed to
/// <see cref="Av1Cdef.Apply"/> directly; the winning candidates' filtered content is copied back into them
/// instead, so their own allocation (plain arrays, owned by <see cref="Av1FrameEncoder.Encode"/>) never
/// changes.</para>
/// </summary>
internal static class Av1CdefSearch
{
    /// <summary>
    /// (primary, secondary) pairs applied identically to Y and UV -- secondary strengths are restricted to
    /// {0, 1, 2, 4} (see <see cref="Av1CdefChoice"/>'s remarks on why 3 is unreachable). Index 0 is the real
    /// "no filtering" combo (spec strength 0 disables both passes) -- included explicitly so the real per-unit
    /// search below can genuinely choose "off" for a unit alongside every other real candidate, exactly like
    /// real libaom's own candidate tables always do (e.g. the execution guide's own <c>CDEF_FAST_SEARCH_LVL4</c>
    /// example includes <c>(0,0)</c>).
    /// </summary>
    private static readonly (int Pri, int Sec)[] Candidates =
    [
        (0, 0), (1, 0), (2, 1), (4, 1), (4, 2), (8, 2), (8, 4), (12, 4), (15, 4),
    ];

    /// <summary>
    /// Searches for the real per-64x64-unit CDEF strength assignment that minimizes this project's own
    /// Lagrangian RD cost (see this type's own class-level remarks), starting from <paramref name="reconY"/>/
    /// <paramref name="reconU"/>/<paramref name="reconV"/>'s current (already-deblocked) content. Copies the
    /// winning per-unit content back into those same buffers in place, and returns the full result
    /// (<see cref="Av1FrameHeaderWriter.Write"/>'s <c>cdef</c> parameter, plus the per-unit assignment
    /// <see cref="Av1TileEncoder"/>'s <c>WriteCdef</c> needs to signal each unit's own <c>cdef_idx</c>).
    /// </summary>
    public static Av1CdefSearchResult SearchAndApply(
        int[] reconY, int[]? reconU, int[]? reconV,
        int[] sourceY, int[]? sourceU, int[]? sourceV,
        int width, int height, int chromaWidth, int chromaHeight,
        bool monoChrome, int baseQIdx, int loopFilterLevel0, int loopFilterLevel1, int loopFilterLevelU, int loopFilterLevelV)
    {
        int miCols = 2 * ((width + 7) >> 3);
        int miRows = 2 * ((height + 7) >> 3);
        int lumaLen = width * height;
        int chromaLen = chromaWidth * chromaHeight;

        // Non-lossless canvases are always already padded to a 64-pixel multiple (Av1FrameEncoder's own
        // sbPixels = 64 padding) -- no partial-unit rounding needed at the frame edge.
        int unitCols = width / 64;
        int unitRows = height / 64;
        int unitCount = unitCols * unitRows;

        var seq = BuildSequenceHeader(monoChrome);
        int numCandidates = Candidates.Length;

        // Phase 1: per-unit candidate MSE table. Each candidate is applied frame-wide (mathematically
        // equivalent to applying it only within one unit and reading that unit back, since CDEF only ever
        // reads a small local neighborhood -- a single global strength choice IS the per-unit behavior when
        // applied everywhere), then sliced into per-unit SSE contributions. Filtered buffers themselves are
        // not retained past this phase (bounded memory -- see this type's own class remarks); the few kept
        // combos the RD decision below actually needs are cheaply re-filtered once more in Phase 3.
        var candidateMse = new long[numCandidates][];
        for (int i = 0; i < numCandidates; i++)
        {
            var (pri, sec) = Candidates[i];
            var choice = new Av1CdefChoice(Damping: 3, YPriStrength: pri, YSecStrength: sec, UvPriStrength: pri, UvSecStrength: sec);
            candidateMse[i] = ApplyAndComputePerUnitSse(choice, seq, reconY, reconU, reconV, sourceY, sourceU, sourceV, width, height, chromaWidth, chromaHeight, monoChrome, baseQIdx, loopFilterLevel0, loopFilterLevel1, loopFilterLevelU, loopFilterLevelV, miCols, miRows, lumaLen, chromaLen, unitCols, unitRows, filteredYOut: null, filteredUOut: null, filteredVOut: null);
        }

        // Phase 2: greedy clustering per candidate bit-count (0..3, i.e. 1/2/4/8 kept combos -- CDEF_MAX_STRENGTHS),
        // then a real Lagrangian RD cost picks the winning bit-count. Pure min-MSE selection within each
        // bit-count (real libaom's own search_one/joint_strength_search); rate only enters the final
        // across-bit-count comparison.
        double lambda = Av1RdCost.QIndexToLambda(baseQIdx);
        int bestBits = 0;
        int[] bestKept = [0]; // candidate index 0 is (0,0) -- a genuinely safe, always-valid fallback.
        long bestRdCost = long.MaxValue;

        for (int bits = 0; bits <= 3 && (1 << bits) <= numCandidates; bits++)
        {
            int nbStrengths = 1 << bits;
            int[] kept = new int[nbStrengths];
            bool[] isKept = new bool[numCandidates];
            long[] bestSoFar = new long[unitCount];
            Array.Fill(bestSoFar, long.MaxValue);

            for (int slot = 0; slot < nbStrengths; slot++)
            {
                int bestCandidate = -1;
                long bestSum = long.MaxValue;
                for (int cand = 0; cand < numCandidates; cand++)
                {
                    if (isKept[cand])
                    {
                        continue;
                    }

                    long sum = 0;
                    var mse = candidateMse[cand];
                    for (int u = 0; u < unitCount; u++)
                    {
                        long m = Math.Min(bestSoFar[u], mse[u]);
                        sum += m;
                    }

                    if (sum < bestSum)
                    {
                        bestSum = sum;
                        bestCandidate = cand;
                    }
                }

                kept[slot] = bestCandidate;
                isKept[bestCandidate] = true;
                var bestCandMse = candidateMse[bestCandidate];
                for (int u = 0; u < unitCount; u++)
                {
                    bestSoFar[u] = Math.Min(bestSoFar[u], bestCandMse[u]);
                }
            }

            long totalMse = 0;
            for (int u = 0; u < unitCount; u++)
            {
                totalMse += bestSoFar[u];
            }

            // real libaom's own total_bits formula (pickcdef.c): sb_count * bitCount (one cdef_idx literal
            // per unit) + nb_strengths * 6 * (planes>1?2:1) (each kept combo's own header cost). sb_count is
            // unitCount here -- see this type's own class remarks on why the real skip-exemption rule is
            // currently a guaranteed no-op given this project's own non-lossless Skips semantics.
            long totalBits = ((long)unitCount * bits) + ((long)nbStrengths * 6 * (monoChrome ? 1 : 2));
            long rdCost = Av1RdCost.CombineCost(totalMse, totalBits, lambda);

            if (rdCost < bestRdCost)
            {
                bestRdCost = rdCost;
                bestBits = bits;
                bestKept = kept;
            }
        }

        // Phase 3: final per-unit remap (whichever kept combo minimizes this unit's own MSE) and real
        // application -- re-filter just the winning kept combos (at most 8) and composite each unit's own
        // footprint from whichever one actually won there.
        int[] unitIdx = new int[unitCount];
        for (int u = 0; u < unitCount; u++)
        {
            int bestSlot = 0;
            long bestMse = candidateMse[bestKept[0]][u];
            for (int slot = 1; slot < bestKept.Length; slot++)
            {
                long m = candidateMse[bestKept[slot]][u];
                if (m < bestMse)
                {
                    bestMse = m;
                    bestSlot = slot;
                }
            }

            unitIdx[u] = bestSlot;
        }

        var combos = new Av1CdefChoice[bestKept.Length];
        var filteredYPerSlot = new int[bestKept.Length][];
        var filteredUPerSlot = new int[bestKept.Length][];
        var filteredVPerSlot = new int[bestKept.Length][];
        for (int slot = 0; slot < bestKept.Length; slot++)
        {
            var (pri, sec) = Candidates[bestKept[slot]];
            var choice = new Av1CdefChoice(Damping: 3, YPriStrength: pri, YSecStrength: sec, UvPriStrength: pri, UvSecStrength: sec);
            combos[slot] = choice;

            int[] filteredY = new int[lumaLen];
            int[]? filteredU = monoChrome ? null : new int[chromaLen];
            int[]? filteredV = monoChrome ? null : new int[chromaLen];
            ApplyAndComputePerUnitSse(choice, seq, reconY, reconU, reconV, sourceY, sourceU, sourceV, width, height, chromaWidth, chromaHeight, monoChrome, baseQIdx, loopFilterLevel0, loopFilterLevel1, loopFilterLevelU, loopFilterLevelV, miCols, miRows, lumaLen, chromaLen, unitCols, unitRows, filteredY, filteredU, filteredV);
            filteredYPerSlot[slot] = filteredY;
            filteredUPerSlot[slot] = filteredU!;
            filteredVPerSlot[slot] = filteredV!;
        }

        for (int u = 0; u < unitCount; u++)
        {
            int slot = unitIdx[u];
            CopyUnitFootprint(filteredYPerSlot[slot], reconY, u, unitCols, width, height, unitPixels: 64);
            if (!monoChrome)
            {
                CopyUnitFootprint(filteredUPerSlot[slot], reconU!, u, unitCols, chromaWidth, chromaHeight, unitPixels: 32);
                CopyUnitFootprint(filteredVPerSlot[slot], reconV!, u, unitCols, chromaWidth, chromaHeight, unitPixels: 32);
            }
        }

        return new Av1CdefSearchResult
        {
            FrameParams = new Av1CdefFrameParams { Damping = 3, Bits = bestBits, Combos = combos },
            UnitIdx = bestBits == 0 ? null : unitIdx,
            UnitCols = unitCols,
            UnitRows = unitRows,
        };
    }

    /// <summary>
    /// Applies one candidate <paramref name="choice"/> frame-wide via the real decoder filter and returns its
    /// per-unit SSE-against-source array. When <paramref name="filteredYOut"/>/<paramref name="filteredUOut"/>/
    /// <paramref name="filteredVOut"/> are non-null, also copies the full filtered planes into them (Phase 3's
    /// own "keep the winning combos' real filtered content for compositing" need); left <see langword="null"/>
    /// during Phase 1's candidate-MSE-only sweep, where retaining every candidate's own full filtered planes
    /// would cost <c>numCandidates</c> full frame buffers for no benefit (see this type's own class remarks).
    /// </summary>
    private static long[] ApplyAndComputePerUnitSse(
        Av1CdefChoice choice, Av1SequenceHeader seq,
        int[] reconY, int[]? reconU, int[]? reconV,
        int[] sourceY, int[]? sourceU, int[]? sourceV,
        int width, int height, int chromaWidth, int chromaHeight,
        bool monoChrome, int baseQIdx, int loopFilterLevel0, int loopFilterLevel1, int loopFilterLevelU, int loopFilterLevelV,
        int miCols, int miRows, int lumaLen, int chromaLen, int unitCols, int unitRows,
        int[]? filteredYOut, int[]? filteredUOut, int[]? filteredVOut)
    {
        var (trialY, trialU, trialV) = RentAndCopy(reconY, reconU, reconV, lumaLen, chromaLen);
        var frame = BuildFrameHeaderForChoice(width, height, monoChrome, baseQIdx, loopFilterLevel0, loopFilterLevel1, loopFilterLevelU, loopFilterLevelV, choice);
        var result = BuildDecodeResult(seq, frame, trialY, trialU, trialV, miCols, miRows, width, height, chromaWidth, chromaHeight);
        Av1Cdef.Apply(result);

        long[] unitMse = new long[unitCols * unitRows];
        AccumulatePerUnitSse(unitMse, result.Planes[0], sourceY, width, height, unitCols, unitPixels: 64);
        if (!monoChrome)
        {
            AccumulatePerUnitSse(unitMse, result.Planes[1], sourceU, chromaWidth, chromaHeight, unitCols, unitPixels: 32);
            AccumulatePerUnitSse(unitMse, result.Planes[2], sourceV, chromaWidth, chromaHeight, unitCols, unitPixels: 32);
        }

        if (filteredYOut is not null)
        {
            Array.Copy(result.Planes[0], filteredYOut, lumaLen);
            if (!monoChrome)
            {
                Array.Copy(result.Planes[1], filteredUOut!, chromaLen);
                Array.Copy(result.Planes[2], filteredVOut!, chromaLen);
            }
        }

        ReturnPlanes(result, monoChrome);
        return unitMse;
    }

    /// <summary>Adds this plane's own per-unit squared-error contribution into <paramref name="unitMse"/> (already sized <c>unitCols*unitRows</c>, shared additively across planes -- matching the frame-wide search's own single combined Y+U+V total).</summary>
    private static void AccumulatePerUnitSse(long[] unitMse, int[] filtered, int[]? source, int planeWidth, int planeHeight, int unitCols, int unitPixels)
    {
        if (source is null)
        {
            return;
        }

        for (int y = 0; y < planeHeight; y++)
        {
            int unitRow = y / unitPixels;
            int rowBase = y * planeWidth;
            int unitRowBase = unitRow * unitCols;
            for (int x = 0; x < planeWidth; x++)
            {
                int diff = filtered[rowBase + x] - source[rowBase + x];
                unitMse[unitRowBase + (x / unitPixels)] += (long)diff * diff;
            }
        }
    }

    /// <summary>Copies unit <paramref name="unitIndex"/>'s own <paramref name="unitPixels"/>-sized footprint from <paramref name="filtered"/> into <paramref name="dest"/> in place.</summary>
    private static void CopyUnitFootprint(int[] filtered, int[] dest, int unitIndex, int unitCols, int planeWidth, int planeHeight, int unitPixels)
    {
        int unitRow = unitIndex / unitCols;
        int unitCol = unitIndex % unitCols;
        int startY = unitRow * unitPixels;
        int startX = unitCol * unitPixels;
        int endY = Math.Min(startY + unitPixels, planeHeight);
        int endX = Math.Min(startX + unitPixels, planeWidth);

        for (int y = startY; y < endY; y++)
        {
            int rowBase = y * planeWidth;
            Array.Copy(filtered, rowBase + startX, dest, rowBase + startX, endX - startX);
        }
    }

    private static (int[] Y, int[]? U, int[]? V) RentAndCopy(int[] y, int[]? u, int[]? v, int lumaLen, int chromaLen)
    {
        int[] rentedY = ArrayPool<int>.Shared.Rent(lumaLen);
        Array.Copy(y, rentedY, lumaLen);

        if (u is null)
        {
            return (rentedY, null, null);
        }

        int[] rentedU = ArrayPool<int>.Shared.Rent(chromaLen);
        Array.Copy(u, rentedU, chromaLen);
        int[] rentedV = ArrayPool<int>.Shared.Rent(chromaLen);
        Array.Copy(v!, rentedV, chromaLen);
        return (rentedY, rentedU, rentedV);
    }

    /// <summary>Returns every plane <see cref="Av1Cdef.Apply"/> replaced <see cref="Av1FrameDecodeResult.Planes"/> with -- always pool-rented by <see cref="Av1Cdef.Apply"/> itself (see this type's own class-level ownership remarks), regardless of whether the arrays fed into that call originated from <see cref="RentAndCopy"/>.</summary>
    private static void ReturnPlanes(Av1FrameDecodeResult result, bool monoChrome)
    {
        ArrayPool<int>.Shared.Return(result.Planes[0]);
        if (!monoChrome)
        {
            ArrayPool<int>.Shared.Return(result.Planes[1]);
            ArrayPool<int>.Shared.Return(result.Planes[2]);
        }
    }

    /// <summary>Same fixed-configuration <see cref="Av1SequenceHeader"/> shape as <see cref="Av1InLoopFilterSearch"/>'s own builder, except <c>EnableCdef</c> is always <see langword="true"/> here regardless of what the real sequence header ends up signaling -- this search only ever runs for the CDEF filter, which needs it on to do anything at all (see <see cref="Av1Cdef.Apply"/>'s own early-return gate).</summary>
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
        EnableCdef = true,
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

    /// <summary>Resolves the real <see cref="Av1FrameHeader"/> a candidate <paramref name="cdef"/> choice would produce, the same throwaway-<see cref="Av1BitWriter"/> reuse <see cref="Av1InLoopFilterSearch"/>'s identically-named method uses and for the same reason (reuse <see cref="Av1FrameHeaderWriter.Write"/>'s already-correct field construction instead of a second copy of it). Always exactly one combo (<c>Bits = 0</c>): every real per-candidate MSE trial this type runs applies ONE choice frame-wide (see this type's own remarks on why that's equivalent to a real per-unit application), never the final multi-combo result.</summary>
    private static Av1FrameHeader BuildFrameHeaderForChoice(int width, int height, bool monoChrome, int baseQIdx, int loopFilterLevel0, int loopFilterLevel1, int loopFilterLevelU, int loopFilterLevelV, Av1CdefChoice cdef)
    {
        var scratchWriter = new Av1BitWriter();
        var frameParams = new Av1CdefFrameParams { Damping = cdef.Damping, Bits = 0, Combos = [cdef] };
        return Av1FrameHeaderWriter.Write(scratchWriter, width, height, monoChrome, baseQIdx, lossless: false, loopFilterLevel0, enableCdef: true, frameParams, allowScreenContentTools: false, allowIntrabc: false, reducedTxSet: true, loopFilterLevel1, loopFilterLevelU, loopFilterLevelV);
    }

    private static Av1FrameDecodeResult BuildDecodeResult(Av1SequenceHeader seq, Av1FrameHeader frame, int[] y, int[]? u, int[]? v, int miCols, int miRows, int width, int height, int chromaWidth, int chromaHeight)
    {
        int frameSize = miCols * miRows;

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

            // CDEF bypasses an 8x8 unit only when it and its 3 neighbor mi positions are *all* skip (spec
            // §7.15.1) -- this encoder's non-lossless leaves never set Skips at all (that flag only ever
            // becomes true for lossless-only features, palette/exact-match IntraBC -- see
            // Av1TileEncoder.EncodeLeaf's own Skips assignment remarks), so an all-false array here exactly
            // matches every real non-lossless leaf's real skip state; CDEF filters every unit accordingly.
            Skips = new bool[frameSize],
            SegmentIds = new int[frameSize],
            DeltaLfs = [new int[frameSize], new int[frameSize], new int[frameSize], new int[frameSize]],
            LoopfilterTxSizes = [[], [], []],
            LoopfilterTxSizeStrides = [0, 0, 0],

            // Every unit resolves to strength-combo index 0 for this single-combo per-candidate evaluation
            // trial (this type's own real per-unit remap happens afterward, in C# -- see SearchAndApply).
            CdefIdx = new int[frameSize],
            RestorationUnits = [null, null, null],
            MinSymbolMaxBitsAtExit = 0,
        };
    }
}

/// <summary>
/// One candidate CDEF search's real result: the frame-wide <see cref="Av1CdefFrameParams"/>
/// <see cref="Av1FrameHeaderWriter.Write"/> needs, plus the per-64x64-unit combo assignment
/// <see cref="Av1TileEncoder"/>'s own per-leaf <c>cdef_idx</c> write needs (spec's real per-unit adaptivity --
/// see <see cref="Av1CdefSearch"/>'s own class remarks for the full algorithm).
/// </summary>
internal sealed class Av1CdefSearchResult
{
    public required Av1CdefFrameParams FrameParams { get; init; }

    /// <summary><see langword="null"/> whenever <see cref="Av1CdefFrameParams.Bits"/> == 0 (every unit trivially uses the single combo, no literal ever needs writing -- see <see cref="Av1TileEncoder"/>'s <c>WriteCdef</c>); otherwise one combo index per unit, flat <c>[unitRow*UnitCols+unitCol]</c>.</summary>
    public int[]? UnitIdx { get; init; }

    public int UnitCols { get; init; }
    public int UnitRows { get; init; }

    public static readonly Av1CdefSearchResult Off = new() { FrameParams = Av1CdefFrameParams.Off, UnitIdx = null, UnitCols = 0, UnitRows = 0 };
}
