using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Internal;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Encodes one single-tile intra frame: walks the superblock grid (spec §5.11.4 <c>decode_partition()</c>'s
/// write-side mirror). Non-lossless frames still force every 64x64 superblock to split all the way down to
/// a uniform 8x8 leaf grid (this encoder does not implement non-lossless partition-tree RDO -- see
/// <see cref="EncodePartitionForced"/>'s remarks for why). Lossless frames instead run a real (if
/// approximate) rate-distortion partition search (<see cref="DecidePartition"/>, Phase D) at each partition
/// level, comparing the actual WHT-magnitude cost (<see cref="ComputeCandidateCost"/>) of keeping a
/// 16x16/32x32/64x64 region as one leaf against the summed cost of its 4 quadrants, rather than a pure
/// flatness/variance threshold -- this only ever reduces per-leaf mode/skip/partition signaling, never
/// residual coefficient cost by itself (AV1 forces TX_4X4 for every lossless block regardless of
/// coding-block size, so the same number of 4x4 Walsh-Hadamard sub-blocks get coded either way), but a real
/// cost comparison (unlike the pure-flatness heuristic it replaced) can also correctly choose *not* to merge
/// when a coarser single-mode prediction across the merged region would cost more in residual than it saves
/// in signaling -- see <see cref="DecidePartition"/>'s remarks for the full reasoning and the project plan's
/// Phase A/D results for the measurements motivating this. Every leaf gets a real, WHT-magnitude-cost-based
/// intra mode search (13 candidate modes x 7 angle_delta values for the 8 directional ones, plus 5
/// FILTER_INTRA candidates when DC_PRED wins -- see <see cref="EncodeLeaf"/>). Chroma always uses DC_PRED
/// (guaranteeing <c>ComputeTxType</c> derives <c>DctDct</c> for chroma without needing to search or signal a
/// chroma mode/angle at all -- see <c>Av1TxTypeTables.ModeToTxfm[DC_PRED] == DctDct</c>; CFL is spec-illegal
/// for this encoder's lossless-always-4:4:4 RGB output, see the project plan's Phase D notes).
///
/// <para>Requires the luma plane's width/height to already be padded to a multiple
/// of 64 (the caller's job -- see <c>Av1FrameEncoder</c>) so every superblock is a full, in-bounds 64x64
/// block: this eliminates every one of <c>decode_partition()</c>'s edge-of-frame special cases (the
/// <c>hasRows</c>/<c>hasCols</c>-driven HORZ/VERT-forced partitions), which this encoder does not
/// implement.</para>
/// </summary>
internal static class Av1TileEncoder
{
    // All 13 luma intra modes (everything but the chroma-only UV_CFL_PRED) -- Phase D's directional +
    // angle_delta search (see EncodeLeaf) tries every one of the 8 directional modes at every one of AV1's
    // 7 angle_delta values (-3..3, spec MAX_ANGLE_DELTA), not just angle_delta == 0 as this encoder did
    // before Phase D.
    private static readonly int[] CandidateModes =
        [
            Av1IntraMode.DcPred, Av1IntraMode.VPred, Av1IntraMode.HPred, Av1IntraMode.D45Pred, Av1IntraMode.D135Pred,
            Av1IntraMode.D113Pred, Av1IntraMode.D157Pred, Av1IntraMode.D203Pred, Av1IntraMode.D67Pred,
            Av1IntraMode.SmoothPred, Av1IntraMode.SmoothVPred, Av1IntraMode.SmoothHPred, Av1IntraMode.PaethPred,
        ];

    private const int MaxAngleDelta = 3;

    // Matches Av1TileDecoder's own BlockDecodedStride exactly (34 = 32 sub-4x4 units per 128x128-superblock
    // side + a 2-wide margin for the -1 offset both sides of the array need) -- see BlockDecoded's remarks.
    private const int BlockDecodedStride = 34;

    /// <summary>
    /// Encodes the tile and returns its raw byte payload (ready to wrap in a <c>tile_group_obu()</c>).
    /// <paramref name="yPlane"/>/<paramref name="uPlane"/>/<paramref name="vPlane"/> are the true source
    /// planes (already padded); <paramref name="reconY"/>/<paramref name="reconU"/>/<paramref name="reconV"/>
    /// are same-sized output buffers this method fills with the encoder's own local reconstruction (the
    /// same pixels a real decoder will independently reconstruct from this tile's bitstream) -- callers
    /// that only need the encoded bytes may pass fresh same-sized arrays and ignore them.
    /// </summary>
    /// <param name="yPlane">The true (already-padded) source luma plane.</param>
    /// <param name="yWidth">The padded luma plane width.</param>
    /// <param name="yHeight">The padded luma plane height.</param>
    /// <param name="uPlane">The true (already-padded) source chroma-U plane, or <see langword="null"/> when <paramref name="monoChrome"/>.</param>
    /// <param name="vPlane">The true (already-padded) source chroma-V plane, or <see langword="null"/> when <paramref name="monoChrome"/>.</param>
    /// <param name="chromaWidth">The padded chroma plane width.</param>
    /// <param name="chromaHeight">The padded chroma plane height.</param>
    /// <param name="reconY">Output buffer this method fills with the encoder's own local luma reconstruction.</param>
    /// <param name="reconU">Output buffer this method fills with the encoder's own local chroma-U reconstruction, or <see langword="null"/> when <paramref name="monoChrome"/>.</param>
    /// <param name="reconV">Output buffer this method fills with the encoder's own local chroma-V reconstruction, or <see langword="null"/> when <paramref name="monoChrome"/>.</param>
    /// <param name="monoChrome">Whether this frame is monochrome (no chroma planes).</param>
    /// <param name="baseQIdx">The frame's base quantizer index.</param>
    /// <param name="lossless">
    /// When <see langword="true"/>, every block's transform is AV1's lossless Walsh-Hadamard path
    /// (<see cref="Av1ForwardWht"/>) at 4x4 granularity instead of DCT_DCT (a leaf's luma area splits into
    /// <c>(leafSize/4)^2</c> 4x4 transform sub-blocks, matching AV1's forced <c>TX_4X4</c>-when-lossless
    /// rule; chroma follows suit, at 4x4 either way -- see <paramref name="chroma444"/> for how many chroma
    /// sub-blocks that means per leaf). <paramref name="baseQIdx"/> must be 0 in that case (AV1's
    /// coded-lossless trigger).
    /// </param>
    /// <param name="chroma444">
    /// Whether <paramref name="uPlane"/>/<paramref name="vPlane"/> are full luma resolution (4:4:4, no
    /// subsampling) rather than half-resolution in both dimensions (4:2:0, this encoder's only other mode).
    /// Only ever <see langword="true"/> together with <paramref name="lossless"/> and a non-monochrome frame
    /// -- see <see cref="Av1FrameEncoder.Encode"/>'s <c>chroma444</c> gate for why. Each luma leaf gets a
    /// matching same-size chroma region (mirroring luma's own lossless sub-block pattern) instead of 4:2:0's
    /// half-resolution chroma region.
    /// </param>
    public static byte[] EncodeTile(
        int[] yPlane, int yWidth, int yHeight,
        int[]? uPlane, int[]? vPlane, int chromaWidth, int chromaHeight,
        int[] reconY, int[]? reconU, int[]? reconV,
        bool monoChrome, int baseQIdx, bool lossless = false, bool chroma444 = false)
    {
        int miCols = yWidth / 4;
        int miRows = yHeight / 4;

        var cdf = new Av1CdfContext(baseQIdx);
        var symbols = new Av1SymbolEncoder(disableCdfUpdate: false);

        var state = new TileState
        {
            SourceY = yPlane,
            SourceU = uPlane,
            SourceV = vPlane,
            ReconY = reconY,
            ReconU = reconU,
            ReconV = reconV,
            YWidth = yWidth,
            YHeight = yHeight,
            ChromaWidth = chromaWidth,
            ChromaHeight = chromaHeight,
            MonoChrome = monoChrome,
            MiCols = miCols,
            MiRows = miRows,
            BaseQIdx = baseQIdx,
            Lossless = lossless,
            Chroma444 = chroma444,
            Cdf = cdf,
            Symbols = symbols,
            YModes = new int[miCols * miRows],
            MiSizes = new int[miCols * miRows],
            Skips = new bool[miCols * miRows],
            IsInters = new bool[miCols * miRows],
            MvRowsGrid = new int[miCols * miRows],
            MvColsGrid = new int[miCols * miRows],
            Written = new bool[miCols * miRows],
            IntrabcHashIndex = [],
            PositionsBySize = [],
            BlockDecoded = [new bool[BlockDecodedStride * BlockDecodedStride], new bool[BlockDecodedStride * BlockDecodedStride], new bool[BlockDecodedStride * BlockDecodedStride]],
            PartitionDecisions = [],
            PaletteSizesY = new int[miCols * miRows],
            PaletteSizesUV = new int[miCols * miRows],
            PaletteColorsYGrid = new int[miCols * miRows * 8],
            PaletteColorsUGrid = new int[miCols * miRows * 8],
            PaletteColorsY = new int[8],
            PaletteColorsU = new int[8],
            PaletteColorsV = new int[8],
            PaletteColorMap = AvifBufferPool.SharedInt32.Rent(64 * 64),
            YCoeffCtx = new Av1CoefficientWriter.PlaneContext(miCols, miRows),
            UCoeffCtx = monoChrome ? null : new Av1CoefficientWriter.PlaneContext(chroma444 ? miCols : miCols / 2, chroma444 ? miRows : miRows / 2),
            VCoeffCtx = monoChrome ? null : new Av1CoefficientWriter.PlaneContext(chroma444 ? miCols : miCols / 2, chroma444 ? miRows : miRows / 2),

            // Rented once for the whole tile and reused/overwritten across every block below, rather than
            // allocated fresh per block. Pred/BestPred must be sized for the largest leaf this encoder can
            // now produce (64x64 = 4096 elements, lossless only -- see EncodePartitionForced): the whole-leaf
            // mode search predicts into them at the leaf's real size before any residual coding happens.
            // Residual/Coeff/Levels only ever hold one transform block's worth of data at a time (64 elements
            // for this encoder's one remaining non-lossless case -- a fixed 8x8 DCT_DCT leaf -- or 16 for any
            // lossless 4x4 WHT sub-block), so they stay at their original size. ReconDequant alone stays
            // fixed at 64*64 regardless of block size -- see Av1LocalReconstructor.Reconstruct's remarks on
            // why that stride can't shrink.
            Pred = AvifBufferPool.SharedInt32.Rent(64 * 64),
            BestPred = AvifBufferPool.SharedInt32.Rent(64 * 64),
            Residual = AvifBufferPool.SharedInt32.Rent(64),
            Coeff = AvifBufferPool.SharedInt32.Rent(64),
            Levels = AvifBufferPool.SharedInt32.Rent(64),
            ReconDequant = AvifBufferPool.SharedInt32.Rent(64 * 64),
            ReconResidual = AvifBufferPool.SharedInt32.Rent(64),
        };

        try
        {
            for (int r = 0; r < miRows; r += 16)
            {
                for (int c = 0; c < miCols; c += 16)
                {
                    ClearBlockDecodedFlags(state, r, c, sbSize4: 16);
                    EncodePartitionForced(state, r, c, sizeMi: 16);
                }
            }

            return symbols.Flush();
        }
        finally
        {
            AvifBufferPool.SharedInt32.Return(state.Pred);
            AvifBufferPool.SharedInt32.Return(state.BestPred);
            AvifBufferPool.SharedInt32.Return(state.Residual);
            AvifBufferPool.SharedInt32.Return(state.Coeff);
            AvifBufferPool.SharedInt32.Return(state.Levels);
            AvifBufferPool.SharedInt32.Return(state.ReconDequant);
            AvifBufferPool.SharedInt32.Return(state.ReconResidual);
            AvifBufferPool.SharedInt32.Return(state.PaletteColorMap);
        }
    }

    private sealed class TileState
    {
        public required int[] SourceY;
        public required int[]? SourceU;
        public required int[]? SourceV;
        public required int[] ReconY;
        public required int[]? ReconU;
        public required int[]? ReconV;
        public required int YWidth;
        public required int YHeight;
        public required int ChromaWidth;
        public required int ChromaHeight;
        public required bool MonoChrome;
        public required int MiCols;
        public required int MiRows;
        public required int BaseQIdx;
        public required bool Lossless;
        public required bool Chroma444;
        public required Av1CdfContext Cdf;
        public required Av1SymbolEncoder Symbols;
        public required int[] YModes;
        public required int[] MiSizes;
        public required bool[] Skips;

        // IntraBC neighbor context (spec's IsInters/Mvs), write-side mirror of Av1TileDecoder's identically
        // named fields -- see FindMvStack's remarks for why IsInters is only ever true for an IntraBC leaf
        // in this encoder (there is no other way for is_inter to be true). Written mirrors "has this mi
        // position been written for this frame yet" (spec §7.10.2.4's scan point process) -- see ScanPoint's
        // remarks for why this encoder's own quad-split recursion order still needs it (a block deep in a
        // bottom-left quadrant's top-right scan point can land in the not-yet-encoded bottom-right quadrant,
        // despite both quadrants sharing the same row range).
        public required bool[] IsInters;
        public required int[] MvRowsGrid;
        public required int[] MvColsGrid;
        public required bool[] Written;

        // Hash-based IntraBC source search (see FindIntrabcMatch): every fully-encoded leaf's exact pixel
        // content is recorded here (keyed by size + content hash) as a future copy-source candidate for a
        // later leaf of the same size.
        public required Dictionary<(int Size, ulong Hash), List<(int R, int C)>> IntrabcHashIndex;

        // Phase D technique 5: every fully-encoded lossless leaf's position, by size, regardless of content
        // -- unlike IntrabcHashIndex (exact-content lookup), this backs the *approximate*-match search
        // (FindApproximateIntrabcMatch), which needs real candidates to score by residual cost, not just
        // ones that already match exactly.
        public required Dictionary<int, List<(int R, int C)>> PositionsBySize;

        // Write-side mirror of Av1TileDecoder's BlockDecoded tracking (spec's BlockDecoded[][], §5.11.3) --
        // needed so haveAboveRight/haveBelowLeft (spec §7.11.2's edge-extension availability, which directional
        // prediction with a nonzero angle_delta can read past the block's own top-right/bottom-left corner)
        // match what a real decoder will independently compute at the same position, not a conservative
        // always-false guess. See EncodeLeaf/EncodeLosslessLumaResidual's remarks for why getting this wrong
        // is a real correctness bug (not just a missed optimization) once angle_delta is actually searched.
        public required bool[][] BlockDecoded;

        // Phase D "RD-optimal partition search": memoized (keep-as-leaf, cost) decision per (r, c, sizeMi)
        // node, computed by DecidePartition and consumed by EncodePartitionForced -- see DecidePartition's
        // remarks. Keyed by the full (r, c, sizeMi) triple, not just (r, c): the same top-left position is
        // visited at every size level on the way down (a node's leaf-vs-split choice at 32x32 and its
        // leaf-vs-split choice at 16x16 share a top-left corner but are different decisions).
        public required Dictionary<(int R, int C, int SizeMi), (bool KeepAsLeaf, long Cost)> PartitionDecisions;
        public required Av1CoefficientWriter.PlaneContext YCoeffCtx;
        public required Av1CoefficientWriter.PlaneContext? UCoeffCtx;
        public required Av1CoefficientWriter.PlaneContext? VCoeffCtx;

        // Palette mode state (spec §5.11.46/§5.11.47) -- PaletteSizesY/UV and the two color grids are
        // frame-shared neighbor context (mirroring Av1TileDecoder's own identically-named fields exactly,
        // including why only Y and U colors are ever cached -- see Av1TileDecoder.GetPaletteCache's
        // remarks); PaletteColorsY/U/V and PaletteColorMap are per-leaf scratch, reused across every block
        // the same way Pred/BestPred/etc. are.
        public required int[] PaletteSizesY;
        public required int[] PaletteSizesUV;
        public required int[] PaletteColorsYGrid;
        public required int[] PaletteColorsUGrid;
        public required int[] PaletteColorsY;
        public required int[] PaletteColorsU;
        public required int[] PaletteColorsV;
        public required int[] PaletteColorMap;

        // Rented once per EncodeTile call and reused across every block -- see EncodeTile's remarks.
        public required int[] Pred;
        public required int[] BestPred;
        public required int[] Residual;
        public required int[] Coeff;
        public required int[] Levels;
        public required int[] ReconDequant;
        public required int[] ReconResidual;
    }

    private static int BlockSizeFromSizeMi(int sizeMi) => sizeMi switch
    {
        16 => Av1BlockSize.Block64x64,
        8 => Av1BlockSize.Block32x32,
        4 => Av1BlockSize.Block16x16,
        _ => Av1BlockSize.Block8x8,
    };

    /// <summary><c>log2</c> of a leaf's pixel width/height, for <see cref="Av1IntraPrediction.Predict"/>'s <c>log2W</c>/<c>log2H</c> parameters -- e.g. sizeMi 16 (64 pixels) -&gt; 6.</summary>
    private static int PixelLog2(int sizeMi) => sizeMi switch
    {
        16 => 6,
        8 => 5,
        4 => 4,
        _ => 3,
    };

    private static void EncodePartitionForced(TileState s, int r, int c, int sizeMi)
    {
        int bSize = BlockSizeFromSizeMi(sizeMi);

        // decode_partition() reads a partition symbol at every size down to and including 8x8 (only sizes
        // *below* 8x8 skip it) -- Block8x8 is not exempt, it just always has PARTITION_NONE forced here
        // rather than PARTITION_SPLIT, since our leaf floor is 8x8.
        int ctx = PartitionContext(s, r, c, bSize, out int bsl);
        var partitionCdf = bsl switch
        {
            1 => s.Cdf.PartitionW8[ctx],
            2 => s.Cdf.PartitionW16[ctx],
            3 => s.Cdf.PartitionW32[ctx],
            _ => s.Cdf.PartitionW64[ctx],
        };

        if (sizeMi == 2)
        {
            s.Symbols.WriteSymbol(partitionCdf, Av1PartitionType.None);
            EncodeLeaf(s, r, c, sizeMi);
            return;
        }

        // Only lossless mode ever keeps a bigger region as one leaf. Non-lossless keeps this encoder's
        // original behavior of always splitting all the way down to a fixed 8x8 grid: a non-lossless leaf
        // bigger than 8x8 would need a real TX_16X16/TX_32X32/TX_64X64 forward-transform path
        // (Av1ForwardTransform.Forward2D only handles square 4/8/16/32 today, and even 32 isn't wired up as
        // a leaf size below), which is out of scope for this pass -- see the project plan.
        //
        // DecidePartition/EstimateLumaCost (a real WHT-cost-based RD partition search, Phase D technique 6)
        // exist in this file but are DELIBERATELY NOT CALLED here: building them surfaced a genuine,
        // pre-existing bug (present since Phase A, never triggered before because the exactly-flat-only
        // ShouldKeepAsLeaf threshold this replaces essentially never merged non-flat content) somewhere in
        // the "coding block bigger than its 4x4 lossless transform, with a real non-zero residual" path --
        // confirmed independent of every Phase D search addition (angle_delta, FILTER_INTRA, the 8 new
        // directional modes, real haveAboveRight/haveBelowLeft all individually ruled out by disabling each
        // one in turn and still reproducing) and independent of chroma (reproduces monochrome). Root cause
        // not yet found. Falling back to the exactly-flat variance check here is a deliberate, temporary
        // correctness-over-compression choice until that's fixed -- see the project plan's Phase D notes.
        if (s.Lossless && ShouldKeepAsLeafFlatOnly(s, r, c, sizeMi))
        {
            s.Symbols.WriteSymbol(partitionCdf, Av1PartitionType.None);
            EncodeLeaf(s, r, c, sizeMi);
            return;
        }

        s.Symbols.WriteSymbol(partitionCdf, Av1PartitionType.Split);

        int half = sizeMi / 2;
        EncodePartitionForced(s, r, c, half);
        EncodePartitionForced(s, r, c + half, half);
        EncodePartitionForced(s, r + half, c, half);
        EncodePartitionForced(s, r + half, c + half, half);
    }

    // Fixed bit-cost stand-ins (in ComputeCandidateCost's WHT-magnitude units, not real bits -- there is no
    // exact conversion between the two, since ComputeCandidateCost never simulates the actual entropy coder;
    // these are tuned as relative weights, not calibrated bit counts) for the leaf/split-only signaling
    // overhead DecidePartition's cost comparison can't get from ComputeCandidateCost alone: a leaf pays one
    // skip bit, one yMode symbol, (sometimes) an angle_delta, a uv_mode, and palette/filter-intra
    // eligibility bits; a split pays one partition symbol per split point instead. Both are deliberately
    // small relative to typical WHT-magnitude costs (residual cost dominates for anything but the flattest
    // content) -- their job is only to stop the search from splitting purely-flat regions into needlessly
    // many same-cost-zero leaves, the same role FlatnessVarianceThreshold played before this replaced it.
    private const long LeafSignalingCost = 24;
    private const long SplitSignalingCost = 6;

    /// <summary>
    /// Real (if approximate) rate-distortion partition decision, replacing the old pure-variance flatness
    /// heuristic now that <see cref="ComputeCandidateCost"/> (Phase D technique 4) gives every leaf a real
    /// cost proxy to compare, not just "is this region exactly flat". Recursively compares the cost of
    /// keeping (<paramref name="r"/>, <paramref name="c"/>, <paramref name="sizeMi"/>) as one leaf against
    /// the summed cost of its best-decided 4 quadrants, memoized in <see cref="TileState.PartitionDecisions"/>
    /// so each node's cost is computed exactly once regardless of how many ancestors query it (a parent's
    /// own split-cost comparison, and this same node's real encode pass in
    /// <see cref="EncodePartitionForced"/>, both read the cached result rather than re-deriving it).
    ///
    /// <para>Lossless coding reconstructs bit-exactly regardless of this decision -- every 4x4
    /// Walsh-Hadamard sub-block reconstructs its own region independently of how many of them share one
    /// coding block -- so this only ever affects how many bits the coded output takes, never
    /// correctness.</para>
    ///
    /// <para><b>Known approximation</b>: this estimates every candidate leaf's cost from <see cref="TileState.SourceY"/>
    /// directly, not <see cref="TileState.ReconY"/> -- deliberately, not carelessly. For lossless content
    /// specifically, reconstructed pixels are always bit-identical to source pixels once a block is really
    /// encoded, so this sidesteps a real ordering problem: estimating a "what if we split" cost needs each
    /// of the 4 quadrants' own edge context, including from *sibling* quadrants that haven't actually been
    /// encoded yet at estimation time (only decided), so their true reconstruction doesn't exist yet to read
    /// -- their source pixels already equal what that reconstruction will be, so reading source instead is
    /// exact, not approximate, for the pixel data itself. The one real approximation left is availability
    /// flags (<c>haveAboveRight</c>/<c>haveBelowLeft</c>/<see cref="GetFilterType"/>'s smooth-neighbor check)
    /// for a not-yet-really-encoded sibling, which fall back to their safe "unavailable"/"not smooth"
    /// defaults during estimation even where the real encode will later find them true -- never wrong
    /// (BuildEdges' clamp/replicate fallback is always a legal prediction), just a slightly less-informed
    /// cost estimate for those specific candidates.</para>
    /// </summary>
    /// <summary>
    /// Phase A's original flatness heuristic (exactly-flat regions only, variance == 0), restored as the
    /// active partition decision -- see <see cref="EncodePartitionForced"/>'s remarks for why
    /// <see cref="DecidePartition"/> isn't called instead right now. Lossless coding reconstructs
    /// bit-exactly regardless of leaf size, so this choice only ever affects output size; pinned to exactly
    /// 0 (not "nearly flat") because a genuinely zero-variance region's residual is already ~0 regardless of
    /// how it's grouped into leaves, so merging it can't make the coefficient cost worse -- see the project
    /// plan's Phase A results for the measurements behind this.
    /// </summary>
    private static bool ShouldKeepAsLeafFlatOnly(TileState s, int r, int c, int sizeMi)
    {
        int size = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;

        long sum = 0;
        long sumSq = 0;
        for (int i = 0; i < size; i++)
        {
            int rowBase = ((y + i) * s.YWidth) + x;
            for (int j = 0; j < size; j++)
            {
                int v = s.SourceY[rowBase + j];
                sum += v;
                sumSq += (long)v * v;
            }
        }

        long n = (long)size * size;
        long mean = sum / n;
        long variance = (sumSq / n) - (mean * mean);
        return variance <= 0;
    }

    private static (bool KeepAsLeaf, long Cost) DecidePartition(TileState s, int r, int c, int sizeMi)
    {
        if (s.PartitionDecisions.TryGetValue((r, c, sizeMi), out var cached))
        {
            return cached;
        }

        long costLeaf = EstimateLumaCost(s, r, c, sizeMi) + LeafSignalingCost;

        (bool KeepAsLeaf, long Cost) result;
        if (sizeMi == 2)
        {
            result = (true, costLeaf);
        }
        else
        {
            int half = sizeMi / 2;
            long costSplit = SplitSignalingCost
                + DecidePartition(s, r, c, half).Cost
                + DecidePartition(s, r, c + half, half).Cost
                + DecidePartition(s, r + half, c, half).Cost
                + DecidePartition(s, r + half, c + half, half).Cost;

            result = costLeaf <= costSplit ? (true, costLeaf) : (false, costSplit);
        }

        s.PartitionDecisions[(r, c, sizeMi)] = result;
        return result;
    }

    /// <summary>
    /// The best luma-only WHT-magnitude cost (<see cref="ComputeCandidateCost"/>) achievable for a
    /// <paramref name="sizeMi"/>-sized leaf at (<paramref name="r"/>, <paramref name="c"/>), searched over
    /// the same directional-mode/angle_delta candidates <see cref="EncodeLeaf"/>'s real search does --
    /// deliberately without the filter-intra candidates (<see cref="EncodeLeaf"/>'s own measurements found
    /// those win rarely and by little; skipping them here keeps this partition-decision estimate, which runs
    /// far more often than a real per-leaf search does, cheaper without materially changing which candidate
    /// wins most comparisons). See <see cref="DecidePartition"/>'s remarks for why this reads
    /// <see cref="TileState.SourceY"/> rather than <see cref="TileState.ReconY"/>.
    /// </summary>
    private static long EstimateLumaCost(TileState s, int r, int c, int sizeMi)
    {
        int sizePixels = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;
        bool availU = r > 0;
        bool availL = c > 0;

        // haveAboveRight/haveBelowLeft/filterTypeSmooth are deliberately NOT read from real BlockDecoded/
        // YModes state here, unlike EncodeLeaf's real search -- during DecidePartition's cost-only
        // recursion, a "what if we split" comparison estimates all 4 quadrants' costs before any of them
        // are actually committed, so a quadrant's true availability/neighbor-mode state doesn't exist yet
        // to read (see DecidePartition's remarks on the sibling-ordering approximation this already accepts
        // for pixel data via SourceY). Reading real (partially-stale, encode-order-dependent) state here
        // measurably regressed IntraBC's ability to find same-size matches for structurally identical
        // content at different frame positions (confirmed by RepeatedVerticalStripePattern_Lossless_
        // IntrabcRoundTripsExactlyAndStaysSmall going from <1.5x growth to ~2x on doubled content) --
        // fixed, conservative values keep the cost estimate a function of content and true structural
        // position (availU/availL) only, not of what else happened to be encoded first.
        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);
        var pred = s.Pred;
        int log2Size = PixelLog2(sizeMi);
        long bestCost = long.MaxValue;

        foreach (int mode in CandidateModes)
        {
            bool directional = Av1IntraMode.IsDirectional(mode);
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                Av1IntraPrediction.BuildEdges(above, left, s.SourceY, s.YWidth, x, y, sizePixels, sizePixels, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);
                Av1IntraPrediction.Predict(pred, sizePixels, sizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.YWidth - 1, s.YHeight - 1, x, y, bitDepth: 8);

                long cost = ComputeCandidateCost(s, pred, x, y, sizePixels);
                if (cost < bestCost)
                {
                    bestCost = cost;
                }
            }
        }

        return bestCost;
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.ClearBlockDecodedFlags</c> (spec's <c>clear_block_decoded_flags(r, c, sbSize4)</c>, §5.11.3) -- always <c>use_128x128_superblock == false</c> here (see the class remarks), and this encoder is always single-tile (MiColEnd/MiRowEnd == MiCols/MiRows).</summary>
    private static void ClearBlockDecodedFlags(TileState s, int r, int c, int sbSize4)
    {
        int numPlanes = s.MonoChrome ? 1 : 3;
        for (int plane = 0; plane < numPlanes; plane++)
        {
            int subX = plane > 0 && !s.Chroma444 ? 1 : 0;
            int subY = plane > 0 && !s.Chroma444 ? 1 : 0;
            int sbWidth4 = (s.MiCols - c) >> subX;
            int sbHeight4 = (s.MiRows - r) >> subY;

            for (int y = -1; y <= sbSize4 >> subY; y++)
            {
                for (int x = -1; x <= sbSize4 >> subX; x++)
                {
                    bool value = (y < 0 && x < sbWidth4) || (x < 0 && y < sbHeight4);
                    SetBlockDecoded(s, plane, y, x, value);
                }
            }

            SetBlockDecoded(s, plane, sbSize4 >> subY, -1, false);
        }
    }

    private static bool GetBlockDecoded(TileState s, int plane, int y, int x) => s.BlockDecoded[plane][((y + 1) * BlockDecodedStride) + x + 1];

    private static void SetBlockDecoded(TileState s, int plane, int y, int x, bool value) => s.BlockDecoded[plane][((y + 1) * BlockDecodedStride) + x + 1] = value;

    /// <summary>
    /// Marks a <paramref name="bw4"/>x<paramref name="bh4"/> (in luma 4x4-mi units) region starting at
    /// absolute mi position (<paramref name="r"/>, <paramref name="c"/>) as decoded on the luma plane --
    /// the write-side equivalent of every <c>SetBlockDecoded</c> call <c>Av1TileDecoder.TransformBlock</c>
    /// would make while iterating that same footprint's real 4x4 transform blocks, collapsed into one call
    /// for the coding paths here (palette, IntraBC, non-lossless TX8X8) that predict/reconstruct a whole
    /// region at once instead of transform-block by transform-block.
    /// </summary>
    private static void MarkLumaBlockDecoded(TileState s, int r, int c, int bw4, int bh4)
    {
        int subBlockMiRow = r & 15;
        int subBlockMiCol = c & 15;
        for (int i = 0; i < bh4; i++)
        {
            for (int j = 0; j < bw4; j++)
            {
                SetBlockDecoded(s, 0, subBlockMiRow + i, subBlockMiCol + j, true);
            }
        }
    }

    /// <summary>
    /// Write-side mirror of <c>Av1TileDecoder.GetFilterType</c> (spec's <c>get_filter_type(plane)</c>,
    /// §7.11.2.8), restricted to luma (chroma stays hardcoded DC_PRED for now, which never reaches the
    /// intra-edge-filter/upsample path this value feeds, so it doesn't need this yet). Computed once per
    /// *coding block* from its own above/left neighbor -- not once per 4x4 transform sub-block -- exactly
    /// mirroring the decoder's own <c>_availU</c>/<c>_availL</c>/<c>_miRow</c>/<c>_miCol</c>-based fields,
    /// which are coding-block-scoped, not transform-block-scoped.
    /// </summary>
    private static bool GetFilterType(TileState s, int r, int c, bool availU, bool availL)
    {
        bool aboveSmooth = availU && IsSmoothMode(s, r - 1, c);
        bool leftSmooth = availL && IsSmoothMode(s, r, c - 1);
        return aboveSmooth || leftSmooth;
    }

    private static bool IsSmoothMode(TileState s, int row, int col)
    {
        row = Math.Clamp(row, 0, s.MiRows - 1);
        col = Math.Clamp(col, 0, s.MiCols - 1);
        int mode = s.YModes[(row * s.MiCols) + col];
        return mode is Av1IntraMode.SmoothPred or Av1IntraMode.SmoothVPred or Av1IntraMode.SmoothHPred;
    }

    /// <summary>
    /// Phase D technique 4: a rate proxy for candidate mode/angle/filter-intra comparison, replacing raw SSE
    /// for lossless leaves. SSE only approximates entropy-coded bit cost -- for a lossless coder specifically
    /// (where distortion is always exactly zero once the real residual is added; the only real objective is
    /// minimizing bits), a proxy computed in the *transform domain* the entropy coder actually sees is a
    /// closer stand-in than raw spatial-domain squared error: this sums the L1 magnitude of every 4x4
    /// Walsh-Hadamard coefficient (<see cref="Av1ForwardWht"/>, the real lossless transform -- see
    /// <see cref="EncodeLosslessLumaResidual"/>'s identical per-sub-block transform) the candidate's residual
    /// would actually produce, not a full entropy-cost simulation (no CDF/context modeling), but materially
    /// closer to true bit cost than SSE: AV1's coefficient entropy coding costs roughly log-linearly in
    /// magnitude and near-zero for exact zeros, both of which SSE (quadratic, and blind to whether a
    /// candidate's errors cluster into few zero-heavy 4x4 blocks or spread evenly) doesn't capture. Only used
    /// for lossless -- non-lossless leaves use DCT, not WHT, so this proxy wouldn't match what they actually
    /// entropy-code; SSE remains the (already-established, still-correct) proxy there.
    /// </summary>
    private static long ComputeCandidateCost(TileState s, int[] pred, int x, int y, int sizePixels)
    {
        if (!s.Lossless)
        {
            long sse = 0;
            for (int i = 0; i < sizePixels; i++)
            {
                int rowBase = ((y + i) * s.YWidth) + x;
                int predRowBase = i * sizePixels;
                for (int j = 0; j < sizePixels; j++)
                {
                    int diff = s.SourceY[rowBase + j] - pred[predRowBase + j];
                    sse += (long)diff * diff;
                }
            }

            return sse;
        }

        long cost = 0;
        Span<int> residual = stackalloc int[16];
        Span<int> coeff = stackalloc int[16];
        for (int by = 0; by < sizePixels; by += 4)
        {
            for (int bx = 0; bx < sizePixels; bx += 4)
            {
                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((y + by + i) * s.YWidth) + x + bx;
                    int predRowBase = ((by + i) * sizePixels) + bx;
                    for (int j = 0; j < 4; j++)
                    {
                        residual[(i * 4) + j] = s.SourceY[rowBase + j] - pred[predRowBase + j];
                    }
                }

                Av1ForwardWht.Forward4x4(residual, coeff);
                for (int k = 0; k < 16; k++)
                {
                    cost += Math.Abs(coeff[k]);
                }
            }
        }

        return cost;
    }

    private static int PartitionContext(TileState s, int r, int c, int bSize, out int bsl)
    {
        bsl = Av1BlockTables.MiWidthLog2[bSize];
        bool above = r > 0 && Av1BlockTables.MiWidthLog2[s.MiSizes[((r - 1) * s.MiCols) + c]] < bsl;
        bool left = c > 0 && Av1BlockTables.MiHeightLog2[s.MiSizes[(r * s.MiCols) + c - 1]] < bsl;
        return ((left ? 1 : 0) * 2) + (above ? 1 : 0);
    }

    private static void EncodeLeaf(TileState s, int r, int c, int sizeMi)
    {
        int sizePixels = sizeMi * 4;
        int bSize = BlockSizeFromSizeMi(sizeMi);
        bool availU = r > 0;
        bool availL = c > 0;
        int x = c * 4;
        int y = r * 4;

        int aboveYMode = availU ? s.YModes[((r - 1) * s.MiCols) + c] : Av1IntraMode.DcPred;
        int leftYMode = availL ? s.YModes[(r * s.MiCols) + c - 1] : Av1IntraMode.DcPred;
        int yModeCtx0 = Av1BlockTables.IntraModeContext[aboveYMode];
        int yModeCtx1 = Av1BlockTables.IntraModeContext[leftYMode];

        // Sized to match Av1TileDecoder's own AboveRow/LeftCol buffers (528) rather than a leaf-size-derived
        // formula: the edge-upsample process (Av1IntraPrediction.EdgeUpsample, spec §7.11.2.11) writes at
        // index up to 2*numPx-2 where numPx can reach w+h (not just w or h), so a naive "2*sizePixels"
        // capacity is exactly half of what a 64px leaf's directional-mode search can need once angle_delta
        // is actually varied (Phase D) -- this bit this encoder before angle_delta search existed only
        // because DC/H/V/Smooth/Paeth never reach the upsample path at all.
        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);

        // haveAboveRight/haveBelowLeft, computed from the real BlockDecoded state (mirroring
        // Av1TileDecoder.TransformBlock exactly, treating this whole leaf as one transform block -- the
        // literal case for the non-lossless 8x8 path, which uses bestPred as its final, actually-encoded
        // prediction with no re-derivation afterward). This matters for real correctness, not just search
        // quality, once angle_delta is actually varied (Phase D): a directional predictor can read samples
        // past the block's own top-right/bottom-left corner, and BuildEdges silently clamps/replicates
        // instead when told a neighbor isn't available -- if that disagrees with what a real decoder
        // independently computes for the same position, the two sides predict different pixels from the
        // same signaled mode/angle_delta, corrupting every pixel from there on.
        int subBlockMiRow = r & 15;
        int subBlockMiCol = c & 15;
        bool haveAboveRight = GetBlockDecoded(s, 0, subBlockMiRow - 1, subBlockMiCol + sizeMi);
        bool haveBelowLeft = GetBlockDecoded(s, 0, subBlockMiRow + sizeMi, subBlockMiCol - 1);

        // Computed once per coding block (not per candidate, not per transform sub-block) -- see
        // GetFilterType's remarks for why this must match the decoder's own coding-block-scoped semantics.
        bool filterTypeSmooth = GetFilterType(s, r, c, availU, availL);

        int bestMode = Av1IntraMode.DcPred;
        int bestAngleDelta = 0;
        long bestCost = long.MaxValue;
        var bestPred = s.BestPred;
        var pred = s.Pred;
        int log2Size = PixelLog2(sizeMi);
        int leafElements = sizePixels * sizePixels;

        foreach (int mode in CandidateModes)
        {
            bool directional = Av1IntraMode.IsDirectional(mode);
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                // Rebuilt fresh for every candidate: directional prediction (Av1IntraPrediction.PredictDirectional)
                // filters/upsamples aboveRow/leftCol IN PLACE (spec §7.11.2.11/.12), so reusing one mutated
                // pair of edge arrays across multiple candidates would feed every candidate after the first
                // directional one increasingly corrupted edge data -- including the eventual "winning"
                // candidate, whose bestPred (used directly as the final, actually-encoded prediction for the
                // non-lossless path) would then disagree with what a real decoder's own single fresh
                // BuildEdges-then-Predict call independently reconstructs.
                Av1IntraPrediction.BuildEdges(above, left, s.ReconY, s.YWidth, x, y, sizePixels, sizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);
                Av1IntraPrediction.Predict(pred, sizePixels, sizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.YWidth - 1, s.YHeight - 1, x, y, bitDepth: 8);

                long cost = ComputeCandidateCost(s, pred, x, y, sizePixels);

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                    bestAngleDelta = angleDelta;
                    Array.Copy(pred, bestPred, leafElements);
                }
            }
        }

        // FILTER_INTRA (spec §5.11.24), tried only when DC_PRED already won the search above --
        // filter_intra_mode_info() only ever reads use_filter_intra when YMode == DC_PRED (mirroring
        // TryBuildYPalette's identical restriction just below), so this never overrides a genuinely better
        // directional/smooth/paeth choice, only offers a further alternative for whatever the search already
        // picked. Also gated on PaletteSizeY == 0 per spec -- deferred until usedPalette is known (see the
        // actual use_filter_intra write site) since this encoder always ties Y and UV palette eligibility
        // together (see usedPalette's own remarks), so it isn't needed for the search itself.
        bool bestUseFilterIntra = false;
        int bestFilterIntraMode = 0;
        if (bestMode == Av1IntraMode.DcPred && sizePixels <= 32)
        {
            for (int filterMode = 0; filterMode < 5; filterMode++)
            {
                Av1IntraPrediction.BuildEdges(above, left, s.ReconY, s.YWidth, x, y, sizePixels, sizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);
                Av1IntraPrediction.Predict(pred, sizePixels, sizePixels, log2Size, log2Size, above, left, Av1IntraMode.DcPred, availL, availU, useFilterIntra: true, filterIntraMode: filterMode, angleDelta: 0, enableIntraEdgeFilter: true, filterTypeSmooth, s.YWidth - 1, s.YHeight - 1, x, y, bitDepth: 8);

                long cost = ComputeCandidateCost(s, pred, x, y, sizePixels);

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestUseFilterIntra = true;
                    bestFilterIntraMode = filterMode;
                    Array.Copy(pred, bestPred, leafElements);
                }
            }
        }

        bool hasChroma = !s.MonoChrome;

        // Whether screen-content palette mode (spec §5.11.46/§5.11.47) is even structurally present in the
        // bitstream for this leaf -- true whenever the frame declared allow_screen_content_tools (tied to
        // lossless -- see Av1FrameHeaderWriter) and this leaf's size is palette-eligible, which every leaf
        // this encoder ever produces is (floor 8x8, ceiling 64x64, always within spec's 64x64 palette size
        // cap -- see Av1TileDecoder.AllowPalette). This must exactly match the decoder's own gate: getting
        // it wrong doesn't just miss a compression opportunity, it desyncs every bit after it, since a real
        // decoder either does or doesn't read palette_mode_info() based on this same condition.
        bool paletteStructurallyPresent = s.Lossless;

        // Whether IntraBC's own use_intrabc bit is structurally present -- tied to lossless the same way
        // allow_screen_content_tools is (see Av1FrameHeaderWriter), since this encoder never uses IntraBC
        // outside lossless mode (see IsValidIntrabcSource's remarks). Must exactly match the decoder's own
        // gate for the same reason paletteStructurallyPresent must.
        bool intrabcStructurallyPresent = s.Lossless;

        // Trial-search for an exact-match IntraBC copy source *before* writing anything -- like palette,
        // skip's value depends on whether IntraBC will end up used (see FindIntrabcMatch's remarks), and
        // unlike palette, using IntraBC also means the entire yMode/uv_mode/palette signaling this leaf
        // would otherwise carry is skipped altogether (spec's use_intrabc branch replaces it, not layers on
        // top of it) -- so this has to be decided before the mode search's result is committed to the
        // bitstream at all, not just before the skip bit.
        int intrabcMvRow = 0;
        int intrabcMvCol = 0;
        bool intrabcExact = intrabcStructurallyPresent && FindIntrabcMatch(s, r, c, sizeMi, hasChroma, out intrabcMvRow, out intrabcMvCol);

        // Phase D technique 5: when no byte-exact copy source exists, fall back to a bounded approximate
        // search -- unlike the exact-match path, this leaf still carries a real WHT-coded residual on top
        // of the block-copy prediction (skip = 0), which the decoder already supports unconditionally (see
        // FindApproximateIntrabcMatch's remarks). Only tried when it wasn't already an exact match and the
        // real intra search above found something to beat.
        int approxMvRow = 0;
        int approxMvCol = 0;
        bool intrabcApprox = !intrabcExact && intrabcStructurallyPresent
            && FindApproximateIntrabcMatch(s, r, c, sizeMi, bestCost, out approxMvRow, out approxMvCol);
        if (intrabcApprox)
        {
            intrabcMvRow = approxMvRow;
            intrabcMvCol = approxMvCol;
        }

        bool usedIntrabc = intrabcExact || intrabcApprox;

        // Trial-build this leaf's palette *before* writing anything -- skip's value depends on whether
        // palette will end up used (skip = 1 only when it does, covering every plane at once; see the loop
        // below), so that decision has to be made up front, even though the actual has_palette_y/
        // has_palette_uv/colors/tokens aren't written until after yMode and uv_mode are (spec order). Only
        // attempted when IntraBC isn't already going to be used for this leaf -- IntraBC already guarantees
        // zero residual with less signaling than palette's color table + full index map would cost, so
        // there's no RD scenario where trying palette on top could still win, and skipping the trial avoids
        // stealing DC_PRED-only eligibility from a leaf IntraBC is already covering.
        //
        // Y-palette is only ever attempted when this leaf's whole-block SSE search already picked DC_PRED
        // (spec's own palette-eligibility gate -- palette_mode_info() only ever reads has_palette_y when
        // YMode == DC_PRED -- so this never overrides the mode search, it only offers a cheaper alternative
        // encoding for whatever the search already picked). A genuinely flat leaf's SSE-optimal mode is
        // DC_PRED anyway (a constant predictor has ~zero error against constant content), so this reaches
        // the leaves it needs to without any special-casing of the mode search itself.
        //
        // UV-palette eligibility is checked independently of Y's, matching spec's own independent gate
        // (palette_mode_info()'s UV branch only depends on uv_mode == DC_PRED, which is unconditionally
        // true in this encoder -- CFL isn't implemented -- so it's checked regardless of whether Y's own
        // branch even ran). This encoder still only ever *uses* palette all-or-nothing (both Y and, when
        // hasChroma, UV must fit, so it can always pair the palette prediction with skip = 1 -- see
        // TryBuildYPalette/TryBuildUvPalette's remarks), but the *bits establishing that* -- has_palette_y
        // when eligible, has_palette_uv when eligible -- are structurally required regardless of that
        // all-or-nothing outcome, and must be written even on the leaves that end up not using palette at
        // all (e.g. because Y's mode isn't DC_PRED, only the independent UV bit is even reachable, but it
        // still has to be there).
        int nY = 0;
        int nUv = 0;
        bool yPaletteEligible = !usedIntrabc && paletteStructurallyPresent && bestMode == Av1IntraMode.DcPred
            && TryBuildYPalette(s, x, y, sizePixels, s.PaletteColorsY, out nY);
        bool uvPaletteEligible = !usedIntrabc && paletteStructurallyPresent && hasChroma
            && TryBuildUvPalette(s, x, y, sizePixels, s.PaletteColorsU, s.PaletteColorsV, out nUv);
        bool usedPalette = yPaletteEligible && (!hasChroma || uvPaletteEligible);

        int paletteSizeY = 0;
        int paletteSizeUV = 0;

        // skip: only ever true for a fully palette-covered leaf (every plane predicted exactly, nothing
        // left to correct -- see the block below), so context otherwise only ever reflects palette-leaf
        // neighbors.
        int skipCtx = 0;
        if (availU)
        {
            skipCtx += s.Skips[((r - 1) * s.MiCols) + c] ? 1 : 0;
        }

        if (availL)
        {
            skipCtx += s.Skips[(r * s.MiCols) + c - 1] ? 1 : 0;
        }

        s.Symbols.WriteSymbol(s.Cdf.Skip[skipCtx], (usedPalette || intrabcExact) ? 1 : 0);

        // use_intrabc (spec §5.11.7): structurally present whenever this frame allows it (tied to lossless
        // -- see Av1FrameHeaderWriter), read/written unconditionally for every leaf regardless of outcome,
        // exactly like paletteStructurallyPresent's has_palette_y/has_palette_uv bits.
        if (intrabcStructurallyPresent)
        {
            s.Symbols.WriteSymbol(s.Cdf.Intrabc, usedIntrabc ? 1 : 0);
        }

        if (usedIntrabc)
        {
            // use_intrabc's branch (spec §5.11.7) completely replaces yMode/uv_mode/palette signaling --
            // find_mv_stack(0) + assign_mv(0)'s PredMv derivation must exactly match what a real decoder
            // independently computes (see FindMvStackAndPredict's remarks), so the diffMv this writes lands
            // on the same Mv the decoder reconstructs from PredMv + diffMv.
            var (predMvRow, predMvCol) = FindMvStackAndPredict(s, r, c, bSize);
            WriteMv(s, intrabcMvRow, intrabcMvCol, predMvRow, predMvCol);

            if (intrabcExact)
            {
                // reset_block_context(bw4, bh4) (spec §5.11.5): this leaf is skip = 1 (an exact match has
                // zero residual by construction), so none of the coefficient-writing paths run.
                s.YCoeffCtx.Reset(c, sizeMi, r, sizeMi);
                if (hasChroma)
                {
                    int chromaN = s.Chroma444 ? sizeMi : sizeMi / 2;
                    int chromaR4Base = s.Chroma444 ? r : r / 2;
                    int chromaC4Base = s.Chroma444 ? c : c / 2;
                    s.UCoeffCtx!.Reset(chromaC4Base, chromaN, chromaR4Base, chromaN);
                    s.VCoeffCtx!.Reset(chromaC4Base, chromaN, chromaR4Base, chromaN);
                }

                // Reconstruction is exact by construction (FindIntrabcMatch only ever returns a source
                // whose pixels -- luma and, when hasChroma, chroma -- already verified byte-identical to
                // this leaf's own source), so copying straight from source is simpler than -- and produces
                // identical results to -- running the decoder's own block-copy/subpel-blend prediction here
                // too.
                for (int i = 0; i < sizePixels; i++)
                {
                    Array.Copy(s.SourceY, ((y + i) * s.YWidth) + x, s.ReconY, ((y + i) * s.YWidth) + x, sizePixels);
                }

                if (hasChroma)
                {
                    for (int i = 0; i < sizePixels; i++)
                    {
                        int rowOffset = ((y + i) * s.ChromaWidth) + x;
                        Array.Copy(s.SourceU!, rowOffset, s.ReconU!, rowOffset, sizePixels);
                        Array.Copy(s.SourceV!, rowOffset, s.ReconV!, rowOffset, sizePixels);
                    }
                }
            }
            else
            {
                // Approximate match (skip = 0): the block-copy prediction isn't pixel-exact, so a real WHT
                // residual is coded on top, exactly like EncodeLosslessLumaResidual's intra case -- see
                // EncodeIntrabcResidual's remarks for why no decoder changes were needed for this.
                EncodeIntrabcResidual(s, r, c, x, y, intrabcMvRow, intrabcMvCol, hasChroma, sizePixels);
            }

            MarkLumaBlockDecoded(s, r, c, sizeMi, sizeMi);
        }
        else
        {
        s.Symbols.WriteSymbol(s.Cdf.IntraFrameYMode[yModeCtx0][yModeCtx1], bestMode);

        if (Av1IntraMode.IsDirectional(bestMode))
        {
            s.Symbols.WriteSymbol(s.Cdf.AngleDelta[bestMode - Av1IntraMode.VPred], bestAngleDelta + MaxAngleDelta);
        }

        if (hasChroma)
        {
            // uv_mode is always signalled when hasChroma, always DC_PRED -- but which CDF table depends
            // on cflAllowed (spec §8.3.2, mirroring Av1TileDecoder.ReadUvMode exactly): non-lossless,
            // this encoder's leaf (always 8x8, forced -- see EncodePartitionForced) always has
            // cflAllowed == true (block size <= 32). Lossless is size-dependent instead:
            // GetPlaneResidualSize(bSize, plane:1, ...) is Block4x4 at 4:2:0 (chroma always coded as one
            // 4x4 sub-block per luma 4x4, matching luma 1:1 -- see EncodeChromaRegion) for any leaf
            // size, but equals the leaf's own luma block size at 4:4:4 (chroma matches luma's leaf size
            // 1:1 there too), so cflAllowed flips to false specifically for lossless + 4:4:4. Getting
            // this CDF wrong doesn't just compress worse -- it silently desyncs the entropy decoder
            // against any real AV1 decoder, since CFL-allowed-ness picks which adaptive probability
            // table the very next symbol is read from. Chroma444 and non-lossless never co-occur in
            // this encoder (see Av1FrameEncoder.Encode's chroma444 gate), so the non-lossless branch
            // never needs to consult it.
            bool cflAllowed = s.Lossless
                ? Av1BlockTables.GetPlaneResidualSize(bSize, 1, !s.Chroma444, !s.Chroma444) == Av1BlockSize.Block4x4
                : true;
            var uvModeCdf = cflAllowed ? s.Cdf.UvModeCflAllowed[bestMode] : s.Cdf.UvModeCflNotAllowed[bestMode];
            s.Symbols.WriteSymbol(uvModeCdf, Av1IntraMode.DcPred);
        }

        if (paletteStructurallyPresent)
        {
            int bsizeCtx = GetPaletteBsizeCtx(bSize);

            if (bestMode == Av1IntraMode.DcPred)
            {
                int paletteModeCtx = GetPaletteModeCtx(s, r, c, availU, availL);
                s.Symbols.WriteSymbol(s.Cdf.PaletteYMode[bsizeCtx][paletteModeCtx], usedPalette ? 1 : 0);
                if (usedPalette)
                {
                    s.Symbols.WriteSymbol(s.Cdf.PaletteYSize[bsizeCtx], nY - 2);
                    WritePaletteColorsY(s, s.PaletteColorsY, nY, r, c, availU, availL);
                    paletteSizeY = nY;
                }
            }

            if (hasChroma)
            {
                // paletteUvModeCtx = (this leaf's own Y palette size > 0) -- matches
                // Av1TileDecoder.PaletteModeInfo's `_paletteSizeY > 0 ? 1 : 0` exactly: usedPalette already
                // implies bestMode == DC_PRED and a Y palette was just written above whenever it's true, and
                // is false whenever no Y palette was written (paletteSizeY stays 0), so this is equivalent
                // without needing to re-derive it from paletteSizeY.
                int paletteUvModeCtx = usedPalette ? 1 : 0;
                s.Symbols.WriteSymbol(s.Cdf.PaletteUvMode[paletteUvModeCtx], usedPalette ? 1 : 0);
                if (usedPalette)
                {
                    s.Symbols.WriteSymbol(s.Cdf.PaletteUvSize[bsizeCtx], nUv - 2);
                    WritePaletteColorsUv(s, s.PaletteColorsU, s.PaletteColorsV, nUv, r, c, availU, availL);
                    paletteSizeUV = nUv;
                }
            }
        }

        // filter_intra_mode_info() (spec §5.11.24): structurally present exactly when the decoder's own
        // gate (enable_filter_intra && YMode == DC_PRED && PaletteSizeY == 0 && max(bw,bh) <= 32) holds --
        // enable_filter_intra is unconditionally on (see Av1SequenceHeaderWriter), and PaletteSizeY == 0 is
        // exactly !usedPalette here (this encoder never partially uses Y-only palette -- see usedPalette's
        // own remarks). bestUseFilterIntra can only be true when bestMode == DcPred and sizePixels <= 32
        // already (see the search above), so no separate re-check of those two conditions is needed here.
        if (!usedPalette && bestMode == Av1IntraMode.DcPred && sizePixels <= 32)
        {
            s.Symbols.WriteSymbol(s.Cdf.FilterIntra[bSize], bestUseFilterIntra ? 1 : 0);
            if (bestUseFilterIntra)
            {
                s.Symbols.WriteSymbol(s.Cdf.FilterIntraMode, bestFilterIntraMode);
            }
        }

        if (usedPalette)
        {
            // reset_block_context(bw4, bh4) (spec §5.11.5): this leaf is skip = 1, so none of the
            // WriteCoeffs calls below run -- without this, YCoeffCtx/UCoeffCtx/VCoeffCtx would keep
            // whatever an earlier, unrelated leaf last left in this leaf's own above/left slots, feeding a
            // real decoder's matching reset a stale context it never sees on this side.
            s.YCoeffCtx.Reset(c, sizeMi, r, sizeMi);
            if (hasChroma)
            {
                int chromaN = s.Chroma444 ? sizeMi : sizeMi / 2;
                int chromaR4Base = s.Chroma444 ? r : r / 2;
                int chromaC4Base = s.Chroma444 ? c : c / 2;
                s.UCoeffCtx!.Reset(chromaC4Base, chromaN, chromaR4Base, chromaN);
                s.VCoeffCtx!.Reset(chromaC4Base, chromaN, chromaR4Base, chromaN);
            }

            var colorMap = s.PaletteColorMap;
            BuildColorMap(s.SourceY, s.YWidth, x, y, sizePixels, s.PaletteColorsY, nY, colorMap);
            WriteColorMapTokens(s, colorMap, sizePixels, nY, s.Cdf.PaletteYColorIndex);

            // Reconstruction is exact by construction (every source sample in this leaf is, by
            // TryBuildYPalette/TryBuildUvPalette's own <=8-distinct-values check, already one of the
            // palette colors), so copying straight from source is simpler than -- and produces identical
            // results to -- looking each pixel back up through the palette + color map just written.
            for (int i = 0; i < sizePixels; i++)
            {
                Array.Copy(s.SourceY, ((y + i) * s.YWidth) + x, s.ReconY, ((y + i) * s.YWidth) + x, sizePixels);
            }

            if (hasChroma)
            {
                BuildColorMapUv(s, x, y, sizePixels, s.PaletteColorsU, s.PaletteColorsV, nUv, colorMap);
                WriteColorMapTokens(s, colorMap, sizePixels, nUv, s.Cdf.PaletteUvColorIndex);

                for (int i = 0; i < sizePixels; i++)
                {
                    int rowOffset = ((y + i) * s.ChromaWidth) + x;
                    Array.Copy(s.SourceU!, rowOffset, s.ReconU!, rowOffset, sizePixels);
                    Array.Copy(s.SourceV!, rowOffset, s.ReconV!, rowOffset, sizePixels);
                }
            }

            MarkLumaBlockDecoded(s, r, c, sizeMi, sizeMi);
        }
        else
        {
            if (s.Lossless)
            {
                // AV1 forces TX_4X4 for every block when lossless -- the leaf's transform splits into
                // (sizePixels/4)^2 4x4 sub-blocks, each with its own predict-then-reconstruct pass (see the
                // method's remarks on why this can't just reuse bestPred/the whole-leaf residual the
                // non-lossless path below does).
                EncodeLosslessLumaResidual(s, r, c, x, y, bestMode, bestAngleDelta, filterTypeSmooth, bestUseFilterIntra, bestFilterIntraMode, sizePixels);
            }
            else
            {
                // Only ever reached with sizePixels == 8 -- EncodePartitionForced never keeps a non-lossless
                // region bigger than 8x8 as one leaf (see its remarks).
                int[] residual = s.Residual;
                for (int i = 0; i < 64; i++)
                {
                    residual[i] = s.SourceY[((y + (i / 8)) * s.YWidth) + x + (i % 8)] - bestPred[i];
                }

                int[] coeff = s.Coeff;
                Av1ForwardTransform.Forward2D(residual, coeff, 8);
                int[] levels = s.Levels;
                Av1ForwardQuantizer.Quantize(coeff, levels, 8, s.BaseQIdx);

                // Write the prediction into the reconstruction buffer before Reconstruct() adds the
                // residual -- matches Av1TileDecoder's own predict-then-reconstruct-in-place ordering.
                for (int i = 0; i < 8; i++)
                {
                    Array.Copy(bestPred, i * 8, s.ReconY, ((y + i) * s.YWidth) + x, 8);
                }

                // Y transform type: 8x8 (not >= 32x32) with reduced_tx_set always selects TX_SET_INTRA_2;
                // always signal DCT_DCT, index 1 in TxTypeIntraInvSet2 = [IDTX, DCT_DCT, ADST_ADST,
                // DCT_ADST, DCT_ADST]. Only actually written by WriteCoeffs when the block turns out
                // non-all-zero -- see its remarks.
                int txSzSqr = Av1CoeffTables.TxSizeSqr[Av1TxSize.Tx8x8];

                // intraDir: FilterIntraModeToIntraDir[filterIntraMode] when this leaf used FILTER_INTRA
                // (spec's transform_type() context derivation, mirrored from Av1TileDecoder.TransformType --
                // bestMode is always DC_PRED whenever bestUseFilterIntra is true, per the search above, but
                // that's not the same context index unless filterIntraMode itself also happens to map back
                // to DC_PRED).
                int intraDir = bestUseFilterIntra ? Av1TxTypeTables.FilterIntraModeToIntraDir[bestFilterIntraMode] : bestMode;
                void WriteLumaTxType() => s.Symbols.WriteSymbol(s.Cdf.IntraTxTypeSet2[txSzSqr][intraDir], 1);

                // WriteCoeffs takes (x4, y4) -- AV1's convention is x4 = column, y4 = row -- so this is
                // (c, r), not (r, c). Passing them backwards is silently unobservable on any square coding
                // block grid (miCols == miRows, e.g. every image up to 64x64 after padding), since
                // PlaneContext's MaxX4/MaxY4 bounds are then identical too; it only breaks on a genuinely
                // non-square, multi-superblock frame, where the above/left context bookkeeping silently
                // stops updating past whichever axis is shorter in mi-units -- desyncing every block's
                // entropy context (and the whole rest of the tile with it) from exactly that point onward.
                Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 8, ptype: 0, c, r, s.YCoeffCtx, WriteLumaTxType);
                Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, x, y, 8, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual);
                MarkLumaBlockDecoded(s, r, c, sizeMi, sizeMi);
            }
        }
        }

        int leafYMode = usedIntrabc ? Av1IntraMode.DcPred : bestMode;
        for (int dy = 0; dy < sizeMi; dy++)
        {
            int rowIdx = (r + dy) * s.MiCols;
            for (int dx = 0; dx < sizeMi; dx++)
            {
                int idx = rowIdx + c + dx;
                s.YModes[idx] = leafYMode;
                s.MiSizes[idx] = bSize;
                s.Skips[idx] = usedPalette || usedIntrabc;
                s.PaletteSizesY[idx] = usedPalette ? paletteSizeY : 0;
                s.PaletteSizesUV[idx] = usedPalette ? paletteSizeUV : 0;
                int colorBase = idx * 8;
                for (int k = 0; k < 8; k++)
                {
                    s.PaletteColorsYGrid[colorBase + k] = s.PaletteColorsY[k];
                    s.PaletteColorsUGrid[colorBase + k] = s.PaletteColorsU[k];
                }

                s.IsInters[idx] = usedIntrabc;
                s.MvRowsGrid[idx] = usedIntrabc ? intrabcMvRow : 0;
                s.MvColsGrid[idx] = usedIntrabc ? intrabcMvCol : 0;
                s.Written[idx] = true;
            }
        }

        if (hasChroma && !usedPalette && !usedIntrabc)
        {
            EncodeChromaRegion(s, r, c, x, y, sizeMi);
        }

        if (s.Lossless)
        {
            RecordIntrabcHashEntry(s, r, c, sizeMi, hasChroma);
        }
    }

    // ---- IntraBC (spec §5.11.7's use_intrabc branch / §7.10.2 find_mv_stack / §5.11.31 MV syntax) ----
    //
    // Write-side mirror of Av1TileDecoder's own IntraBC subsystem -- see that type's FindMvStack/ReadMv/
    // ReadMvComponent remarks for the general reasoning (GlobalMvs always zero, no temporal scan, extra
    // search degenerates to a zero-fill, force_integer_mv always 1 so mv_class0_fr/hp/fr/hp are never
    // written). The DV *search* itself (deciding WHICH source position to copy from, if any) has no decoder
    // equivalent -- see FindIntrabcMatch's remarks.

    private const int MaxRefMvStackSize = 8;
    private const int RefCatLevel = 640;
    private const int MvBorder = 128;
    private const int IntrabcDelayPixels = 256;
    private const int IntrabcDelaySb64 = 4;
    private const int MvJointHzvnz = 2;
    private const int MvJointHnzvz = 1;
    private const int MvJointHnzvnz = 3;
    private const int MvClass0 = 0;

    private sealed class MvSearchState
    {
        public int NumMvFound;
        public readonly int[,] RefStackMv = new int[MaxRefMvStackSize, 2];
        public readonly int[] WeightStack = new int[MaxRefMvStackSize];
    }

    private static bool IsInsideEnc(TileState s, int row, int col) => row >= 0 && row < s.MiRows && col >= 0 && col < s.MiCols;

    /// <summary>
    /// Finds an exact-pixel-match copy source for the <paramref name="sizeMi"/>-sized leaf at (r, c), if one
    /// exists among leaves already encoded earlier in this tile. Unlike palette (which only ever needs to
    /// notice "few distinct colors within this one block"), IntraBC needs to find a *different* block
    /// elsewhere in the already-encoded frame with identical content -- a real search problem with no
    /// decoder-side equivalent to mirror. This uses a hash index of every previously-encoded leaf's exact
    /// pixel content (<see cref="TileState.IntrabcHashIndex"/>, populated by <see cref="RecordIntrabcHashEntry"/>),
    /// restricted to same-size leaves -- a real motion-search style scan (arbitrary offsets, sub-leaf-sized
    /// blocks) would find more matches but is out of scope for a first pass (see the project plan's own
    /// scoping note that a small candidate-offset search suffices to start).
    ///
    /// Only ever returns a match with an even luma displacement in both axes when this frame has subsampled
    /// chroma (4:2:0): this guarantees the chroma DV (which is the luma DV divided by 2, spec §7.11.3.3)
    /// lands on a whole chroma pixel with zero fractional phase, so the decoder's block-copy prediction
    /// (<see cref="Av1InterPrediction.PredictIntrabc"/>) is a pure copy on every plane -- letting this method
    /// verify the match with a direct pixel comparison instead of having to replicate the decoder's bilinear
    /// blend logic here too. 4:4:4/monochrome have no such restriction (no subsampling to create a
    /// fractional chroma phase in the first place).
    /// </summary>
    private static bool FindIntrabcMatch(TileState s, int r, int c, int sizeMi, bool hasChroma, out int mvRow, out int mvCol)
    {
        mvRow = 0;
        mvCol = 0;
        int sizePixels = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;

        ulong hash = HashBlock(s, x, y, sizePixels, hasChroma);
        if (!s.IntrabcHashIndex.TryGetValue((sizePixels, hash), out var candidates))
        {
            return false;
        }

        foreach (var (srcR, srcC) in candidates)
        {
            int srcX = srcC * 4;
            int srcY = srcR * 4;
            int deltaRow = srcY - y;
            int deltaCol = srcX - x;

            if (hasChroma && !s.Chroma444 && ((deltaRow & 1) != 0 || (deltaCol & 1) != 0))
            {
                continue;
            }

            if (!IsValidIntrabcSource(s, r, c, sizeMi, srcR, srcC))
            {
                continue;
            }

            if (!BlockPixelsEqual(s, x, y, srcX, srcY, sizePixels, hasChroma))
            {
                // Hash collision, not a real match.
                continue;
            }

            mvRow = deltaRow * 8;
            mvCol = deltaCol * 8;
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>is_mv_valid</c>'s IntraBC region-reachability check (spec §6.10.25), specialized: this encoder is
    /// always single-tile (MiRowStart/MiColStart are always 0, MiRowEnd/MiColEnd are always MiRows/MiCols)
    /// and never uses 128x128 superblocks (see <see cref="EncodeTile"/>'s fixed sizeMi=16 top-level loop).
    /// The spec's <c>bw &lt; 8 &amp;&amp; subsampling_x</c> / <c>bh &lt; 8 &amp;&amp; subsampling_y</c> edge
    /// adjustments are omitted: every leaf this encoder ever produces is at least 8x8 (see the class-level
    /// remarks), so they can never apply. Getting this wrong wouldn't break round-tripping through this
    /// project's own decoder (which doesn't enforce it at read time -- see Av1TileDecoder.ReadMv's remarks),
    /// only real-world conformance, so it's implemented in full rather than approximated.
    /// </summary>
    private static bool IsValidIntrabcSource(TileState s, int r, int c, int sizeMi, int srcR, int srcC)
    {
        int bw = sizeMi * 4;
        int bh = sizeMi * 4;
        int srcTopEdge = srcR * 4;
        int srcLeftEdge = srcC * 4;
        int srcBottomEdge = srcTopEdge + bh;
        int srcRightEdge = srcLeftEdge + bw;

        if (srcTopEdge < 0 || srcLeftEdge < 0 || srcBottomEdge > s.MiRows * 4 || srcRightEdge > s.MiCols * 4)
        {
            return false;
        }

        const int sbH = 64;
        int activeSbRow = (r * 4) / sbH;
        int activeSb64Col = (c * 4) >> 6;
        int srcSbRow = (srcBottomEdge - 1) / sbH;
        int srcSb64Col = (srcRightEdge - 1) >> 6;
        int totalSb64PerRow = ((s.MiCols - 1) >> 4) + 1;
        int activeSb64 = (activeSbRow * totalSb64PerRow) + activeSb64Col;
        int srcSb64 = (srcSbRow * totalSb64PerRow) + srcSb64Col;
        if (srcSb64 >= activeSb64 - IntrabcDelaySb64)
        {
            return false;
        }

        const int gradient = 1 + IntrabcDelaySb64; // + use_128x128_superblock, always 0 here
        int wfOffset = gradient * (activeSbRow - srcSbRow);
        return srcSbRow <= activeSbRow && srcSb64Col < activeSb64Col - IntrabcDelaySb64 + wfOffset;
    }

    private const int ApproxSearchWindow = 64;
    private const long ApproxDvSignalingMargin = 64;

    /// <summary>
    /// Phase D technique 5: a bounded approximate-match search, tried only when <see cref="FindIntrabcMatch"/>
    /// found no byte-exact source. Unlike that search (a hash lookup, effectively free), there is no way to
    /// index "close enough" content cheaply, so this scores real candidates directly: the last
    /// <see cref="ApproxSearchWindow"/> same-size leaves already encoded (<see cref="TileState.PositionsBySize"/>,
    /// not the content-hash index), scanned newest-first. Bounded rather than exhaustive over every
    /// same-size leaf in the frame -- an exhaustive search would be O(leaves) per leaf, O(leaves^2) overall,
    /// too slow for a general-purpose encoder on a large image -- which trades away distant repeats for
    /// bounded, predictable cost; recent/local content is also the more common source of real repetition in
    /// screen-content-style graphics (repeated UI chrome, tiled elements) than a single far-away match.
    /// Scored with the same WHT-magnitude proxy <see cref="ComputeCandidateCost"/> uses (luma only -- chroma
    /// cost is real but a second-order term for this comparison, and estimating it here would double the
    /// per-candidate cost for a proxy that's already approximate), then only accepted if it beats
    /// <paramref name="bestIntraCost"/> by more than a fixed margin standing in for DV signaling overhead
    /// this proxy doesn't otherwise account for (mv_joint/class/sign/magnitude bits the intra candidates'
    /// own cost never had to pay).
    /// </summary>
    private static bool FindApproximateIntrabcMatch(TileState s, int r, int c, int sizeMi, long bestIntraCost, out int mvRow, out int mvCol)
    {
        mvRow = 0;
        mvCol = 0;

        int sizePixels = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;

        if (!s.PositionsBySize.TryGetValue(sizePixels, out var positions))
        {
            return false;
        }

        int start = Math.Max(0, positions.Count - ApproxSearchWindow);
        long bestCost = long.MaxValue;
        int bestMvRow = 0;
        int bestMvCol = 0;
        bool found = false;

        for (int idx = positions.Count - 1; idx >= start; idx--)
        {
            var (srcR, srcC) = positions[idx];
            int srcX = srcC * 4;
            int srcY = srcR * 4;
            int deltaRow = srcY - y;
            int deltaCol = srcX - x;

            if ((deltaRow == 0 && deltaCol == 0) || !IsValidIntrabcSource(s, r, c, sizeMi, srcR, srcC))
            {
                continue;
            }

            if (!s.MonoChrome && !s.Chroma444 && ((deltaRow & 1) != 0 || (deltaCol & 1) != 0))
            {
                continue;
            }

            long cost = ComputeIntrabcLumaCost(s, x, y, deltaRow * 8, deltaCol * 8, sizePixels);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestMvRow = deltaRow * 8;
                bestMvCol = deltaCol * 8;
                found = true;
            }
        }

        if (!found || bestCost + ApproxDvSignalingMargin >= bestIntraCost)
        {
            return false;
        }

        mvRow = bestMvRow;
        mvCol = bestMvCol;
        return true;
    }

    /// <summary>Luma-only WHT-magnitude cost of block-copying from (<paramref name="mvRow"/>, <paramref name="mvCol"/>) (spec 1/8th-luma-sample units) instead of the leaf's own source pixels -- see <see cref="ComputeCandidateCost"/>'s remarks for why this proxy, not SSE.</summary>
    private static long ComputeIntrabcLumaCost(TileState s, int x, int y, int mvRow, int mvCol, int sizePixels)
    {
        var pred = s.BestPred;
        Av1InterPrediction.PredictIntrabc(pred, s.ReconY, s.YWidth, x, y, sizePixels, sizePixels, mvRow, mvCol, subX: 0, subY: 0, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);
        return ComputeCandidateCost(s, pred, x, y, sizePixels);
    }

    /// <summary>
    /// Encodes an approximate-match IntraBC leaf's real residual (skip = 0): predicts every plane via the
    /// same shared block-copy predictor the decoder uses (<see cref="Av1InterPrediction.PredictIntrabc"/>),
    /// then WHT-transforms/quantizes/writes/reconstructs each 4x4 sub-block exactly like
    /// <see cref="EncodeLosslessLumaResidual"/>'s intra case does. No decoder changes were needed for this
    /// at all: <c>Av1TileDecoder.TransformBlock</c>'s <c>if (!_skip) { Coeffs(); Reconstruct(); }</c> already
    /// runs unconditionally after any prediction branch, intrabc included, and lossless's own
    /// <c>qindex &lt;= 0</c> short-circuit in <c>TransformType</c> means no <c>tx_type</c> symbol is ever
    /// read either way (matching <c>writeLumaTxType: null</c> below) -- this leaf looks, to the decoder, like
    /// any other lossless coding block with a nonzero residual, just with a different prediction source.
    /// Unlike intra prediction, IntraBC's predictor doesn't depend on progressively-reconstructed neighbor
    /// pixels within this leaf (it reads from an entirely different, already-fully-encoded region), so the
    /// whole leaf's prediction is produced in one call per plane rather than rebuilt per 4x4 sub-block.
    /// </summary>
    private static void EncodeIntrabcResidual(TileState s, int r, int c, int x, int y, int mvRow, int mvCol, bool hasChroma, int sizePixels)
    {
        var pred = s.Pred;
        Av1InterPrediction.PredictIntrabc(pred, s.ReconY, s.YWidth, x, y, sizePixels, sizePixels, mvRow, mvCol, subX: 0, subY: 0, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);

        int n = sizePixels / 4;
        for (int dr = 0; dr < n; dr++)
        {
            for (int dc = 0; dc < n; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                int subR = r + dr;
                int subC = c + dc;

                var residual = s.Residual;
                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((subY + i) * s.YWidth) + subX;
                    int predRowBase = ((dr * 4) + i) * sizePixels + (dc * 4);
                    for (int j = 0; j < 4; j++)
                    {
                        residual[(i * 4) + j] = s.SourceY[rowBase + j] - pred[predRowBase + j];
                    }
                }

                var coeff = s.Coeff;
                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                var levels = s.Levels;
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                for (int i = 0; i < 4; i++)
                {
                    Array.Copy(pred, (((dr * 4) + i) * sizePixels) + (dc * 4), s.ReconY, ((subY + i) * s.YWidth) + subX, 4);
                }

                Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 0, subC, subR, s.YCoeffCtx, writeLumaTxType: null, blockSize: sizePixels);
                Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, subX, subY, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);

                SetBlockDecoded(s, 0, subR & 15, subC & 15, true);
            }
        }

        if (!hasChroma)
        {
            return;
        }

        int chromaSize = s.Chroma444 ? sizePixels : sizePixels / 2;
        int subXc = s.Chroma444 ? 0 : 1;
        int cx = s.Chroma444 ? x : x / 2;
        int cy = s.Chroma444 ? y : y / 2;
        int chromaN = chromaSize / 4;

        foreach (var (source, recon, ctx) in new[]
        {
            (s.SourceU!, s.ReconU!, s.UCoeffCtx!),
            (s.SourceV!, s.ReconV!, s.VCoeffCtx!),
        })
        {
            var cpred = s.BestPred;
            Av1InterPrediction.PredictIntrabc(cpred, recon, s.ChromaWidth, cx, cy, chromaSize, chromaSize, mvRow, mvCol, subXc, subXc, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);

            for (int dr = 0; dr < chromaN; dr++)
            {
                for (int dc = 0; dc < chromaN; dc++)
                {
                    int subCx = cx + (dc * 4);
                    int subCy = cy + (dr * 4);
                    int chromaR4 = (s.Chroma444 ? r : r / 2) + dr;
                    int chromaC4 = (s.Chroma444 ? c : c / 2) + dc;

                    var residual = s.Residual;
                    for (int i = 0; i < 4; i++)
                    {
                        int rowBase = ((subCy + i) * s.ChromaWidth) + subCx;
                        int predRowBase = (((dr * 4) + i) * chromaSize) + (dc * 4);
                        for (int j = 0; j < 4; j++)
                        {
                            residual[(i * 4) + j] = source[rowBase + j] - cpred[predRowBase + j];
                        }
                    }

                    var coeff = s.Coeff;
                    Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                    var levels = s.Levels;
                    Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                    for (int i = 0; i < 4; i++)
                    {
                        Array.Copy(cpred, (((dr * 4) + i) * chromaSize) + (dc * 4), recon, ((subCy + i) * s.ChromaWidth) + subCx, 4);
                    }

                    int chromaBlockSizeArg = chromaN > 1 ? chromaSize : 0;
                    Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 1, chromaC4, chromaR4, ctx, writeLumaTxType: null, blockSize: chromaBlockSizeArg);
                    Av1LocalReconstructor.Reconstruct(recon, s.ChromaWidth, subCx, subCy, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);
                }
            }
        }
    }

    /// <summary>FNV-1a over a leaf's exact source pixel content (luma, plus chroma when <paramref name="hasChroma"/>) -- used only to index <see cref="FindIntrabcMatch"/>'s candidate lookup; every candidate is still verified with a real pixel comparison (<see cref="BlockPixelsEqual"/>) before use, so a hash collision only costs a wasted lookup, never a wrong match.</summary>
    private static ulong HashBlock(TileState s, int x, int y, int size, bool hasChroma)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;

        for (int i = 0; i < size; i++)
        {
            int rowBase = ((y + i) * s.YWidth) + x;
            for (int j = 0; j < size; j++)
            {
                hash = (hash ^ (uint)s.SourceY[rowBase + j]) * prime;
            }
        }

        if (hasChroma)
        {
            int chromaX = s.Chroma444 ? x : x / 2;
            int chromaY = s.Chroma444 ? y : y / 2;
            int chromaSize = s.Chroma444 ? size : size / 2;
            for (int i = 0; i < chromaSize; i++)
            {
                int rowBase = ((chromaY + i) * s.ChromaWidth) + chromaX;
                for (int j = 0; j < chromaSize; j++)
                {
                    hash = (hash ^ (uint)s.SourceU![rowBase + j]) * prime;
                    hash = (hash ^ (uint)s.SourceV![rowBase + j]) * prime;
                }
            }
        }

        return hash;
    }

    private static bool BlockPixelsEqual(TileState s, int x1, int y1, int x2, int y2, int size, bool hasChroma)
    {
        for (int i = 0; i < size; i++)
        {
            int row1 = ((y1 + i) * s.YWidth) + x1;
            int row2 = ((y2 + i) * s.YWidth) + x2;
            for (int j = 0; j < size; j++)
            {
                if (s.SourceY[row1 + j] != s.SourceY[row2 + j])
                {
                    return false;
                }
            }
        }

        if (hasChroma)
        {
            int c1x = s.Chroma444 ? x1 : x1 / 2;
            int c1y = s.Chroma444 ? y1 : y1 / 2;
            int c2x = s.Chroma444 ? x2 : x2 / 2;
            int c2y = s.Chroma444 ? y2 : y2 / 2;
            int chromaSize = s.Chroma444 ? size : size / 2;
            for (int i = 0; i < chromaSize; i++)
            {
                int row1 = ((c1y + i) * s.ChromaWidth) + c1x;
                int row2 = ((c2y + i) * s.ChromaWidth) + c2x;
                for (int j = 0; j < chromaSize; j++)
                {
                    if (s.SourceU![row1 + j] != s.SourceU![row2 + j] || s.SourceV![row1 + j] != s.SourceV![row2 + j])
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>Records a just-fully-encoded leaf's exact pixel content into <see cref="TileState.IntrabcHashIndex"/> as a future IntraBC copy-source candidate. Only ever called for lossless tiles (see <see cref="EncodeLeaf"/>) -- indexing a non-lossless leaf would be pointless, since this encoder never enables IntraBC outside lossless mode.</summary>
    private static void RecordIntrabcHashEntry(TileState s, int r, int c, int sizeMi, bool hasChroma)
    {
        int sizePixels = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;
        ulong hash = HashBlock(s, x, y, sizePixels, hasChroma);
        var key = (sizePixels, hash);
        if (!s.IntrabcHashIndex.TryGetValue(key, out var list))
        {
            list = [];
            s.IntrabcHashIndex[key] = list;
        }

        list.Add((r, c));

        if (!s.PositionsBySize.TryGetValue(sizePixels, out var positions))
        {
            positions = [];
            s.PositionsBySize[sizePixels] = positions;
        }

        positions.Add((r, c));
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.FindMvStack</c> + <c>AssignMv</c>'s PredMv derivation, isCompound=0 -- see that method's remarks for the full reasoning (identical here, just reading/writing <see cref="TileState"/>'s grids instead of instance fields).</summary>
    private static (int Row, int Col) FindMvStackAndPredict(TileState s, int r, int c, int bSize)
    {
        int bw4 = Av1BlockTables.Num4x4BlocksWide[bSize];
        int bh4 = Av1BlockTables.Num4x4BlocksHigh[bSize];
        var mv = new MvSearchState();

        ScanRow(s, mv, r, c, bw4, -1);
        ScanCol(s, mv, r, c, bh4, -1);
        if (Math.Max(bw4, bh4) <= 16)
        {
            ScanPoint(s, mv, r, c, -1, bw4);
        }

        int numNearest = mv.NumMvFound;
        for (int idx = 0; idx < numNearest; idx++)
        {
            mv.WeightStack[idx] += RefCatLevel;
        }

        ScanPoint(s, mv, r, c, -1, -1);
        ScanRow(s, mv, r, c, bw4, -3);
        ScanCol(s, mv, r, c, bh4, -3);
        if (bh4 > 1)
        {
            ScanRow(s, mv, r, c, bw4, -5);
        }

        if (bw4 > 1)
        {
            ScanCol(s, mv, r, c, bh4, -5);
        }

        SortStack(mv, 0, numNearest);
        SortStack(mv, numNearest, mv.NumMvFound);

        if (mv.NumMvFound < 2)
        {
            for (int idx = mv.NumMvFound; idx < 2; idx++)
            {
                mv.RefStackMv[idx, 0] = 0;
                mv.RefStackMv[idx, 1] = 0;
            }
        }

        int bw = Av1BlockTables.BlockWidth(bSize);
        int bh = Av1BlockTables.BlockHeight(bSize);
        for (int idx = 0; idx < mv.NumMvFound; idx++)
        {
            mv.RefStackMv[idx, 0] = ClampMvRow(mv.RefStackMv[idx, 0], MvBorder + (bh * 8), r, bSize, s.MiRows);
            mv.RefStackMv[idx, 1] = ClampMvCol(mv.RefStackMv[idx, 1], MvBorder + (bw * 8), c, bSize, s.MiCols);
        }

        int predMvRow = mv.RefStackMv[0, 0];
        int predMvCol = mv.RefStackMv[0, 1];
        if (predMvRow == 0 && predMvCol == 0)
        {
            predMvRow = mv.RefStackMv[1, 0];
            predMvCol = mv.RefStackMv[1, 1];
        }

        if (predMvRow == 0 && predMvCol == 0)
        {
            const int sbSize4 = 16; // Num_4x4_Blocks_High[BLOCK_64X64] -- always 64x64 superblocks here
            if (r - sbSize4 < 0)
            {
                predMvRow = 0;
                predMvCol = -((sbSize4 * 4) + IntrabcDelayPixels) * 8;
            }
            else
            {
                predMvRow = -(sbSize4 * 4 * 8);
                predMvCol = 0;
            }
        }

        return (predMvRow, predMvCol);
    }

    private static void ScanRow(TileState s, MvSearchState mv, int r, int c, int bw4, int deltaRow)
    {
        int end4 = Math.Min(Math.Min(bw4, s.MiCols - c), 16);
        int deltaCol = 0;
        bool useStep16 = bw4 >= 16;
        if (Math.Abs(deltaRow) > 1)
        {
            deltaRow += r & 1;
            deltaCol = 1 - (c & 1);
        }

        int i = 0;
        while (i < end4)
        {
            int mvRow = r + deltaRow;
            int mvCol = c + deltaCol + i;
            if (!IsInsideEnc(s, mvRow, mvCol))
            {
                break;
            }

            int len = Math.Min(bw4, Av1BlockTables.Num4x4BlocksWide[s.MiSizes[(mvRow * s.MiCols) + mvCol]]);
            if (Math.Abs(deltaRow) > 1)
            {
                len = Math.Max(2, len);
            }

            if (useStep16)
            {
                len = Math.Max(4, len);
            }

            AddRefMvCandidate(s, mv, mvRow, mvCol, len * 2);
            i += len;
        }
    }

    private static void ScanCol(TileState s, MvSearchState mv, int r, int c, int bh4, int deltaCol)
    {
        int end4 = Math.Min(Math.Min(bh4, s.MiRows - r), 16);
        int deltaRow = 0;
        bool useStep16 = bh4 >= 16;
        if (Math.Abs(deltaCol) > 1)
        {
            deltaRow = 1 - (r & 1);
            deltaCol += c & 1;
        }

        int i = 0;
        while (i < end4)
        {
            int mvRow = r + deltaRow + i;
            int mvCol = c + deltaCol;
            if (!IsInsideEnc(s, mvRow, mvCol))
            {
                break;
            }

            int len = Math.Min(bh4, Av1BlockTables.Num4x4BlocksHigh[s.MiSizes[(mvRow * s.MiCols) + mvCol]]);
            if (Math.Abs(deltaCol) > 1)
            {
                len = Math.Max(2, len);
            }

            if (useStep16)
            {
                len = Math.Max(4, len);
            }

            AddRefMvCandidate(s, mv, mvRow, mvCol, len * 2);
            i += len;
        }
    }

    private static void ScanPoint(TileState s, MvSearchState mv, int r, int c, int deltaRow, int deltaCol)
    {
        int mvRow = r + deltaRow;
        int mvCol = c + deltaCol;
        if (IsInsideEnc(s, mvRow, mvCol) && s.Written[(mvRow * s.MiCols) + mvCol])
        {
            AddRefMvCandidate(s, mv, mvRow, mvCol, 4);
        }
    }

    private static void AddRefMvCandidate(TileState s, MvSearchState mv, int mvRow, int mvCol, int weight)
    {
        int idx = (mvRow * s.MiCols) + mvCol;
        if (!s.IsInters[idx])
        {
            return;
        }

        SearchStack(s, mv, mvRow, mvCol, weight);
    }

    private static void SearchStack(TileState s, MvSearchState mv, int mvRow, int mvCol, int weight)
    {
        int idx = (mvRow * s.MiCols) + mvCol;
        int candMvRow = s.MvRowsGrid[idx];
        int candMvCol = s.MvColsGrid[idx];

        for (int i = 0; i < mv.NumMvFound; i++)
        {
            if (mv.RefStackMv[i, 0] == candMvRow && mv.RefStackMv[i, 1] == candMvCol)
            {
                mv.WeightStack[i] += weight;
                return;
            }
        }

        if (mv.NumMvFound < MaxRefMvStackSize)
        {
            mv.RefStackMv[mv.NumMvFound, 0] = candMvRow;
            mv.RefStackMv[mv.NumMvFound, 1] = candMvCol;
            mv.WeightStack[mv.NumMvFound] = weight;
            mv.NumMvFound++;
        }
    }

    private static void SortStack(MvSearchState mv, int start, int end)
    {
        while (end > start)
        {
            int newEnd = start;
            for (int idx = start + 1; idx < end; idx++)
            {
                if (mv.WeightStack[idx - 1] < mv.WeightStack[idx])
                {
                    (mv.WeightStack[idx - 1], mv.WeightStack[idx]) = (mv.WeightStack[idx], mv.WeightStack[idx - 1]);
                    (mv.RefStackMv[idx - 1, 0], mv.RefStackMv[idx, 0]) = (mv.RefStackMv[idx, 0], mv.RefStackMv[idx - 1, 0]);
                    (mv.RefStackMv[idx - 1, 1], mv.RefStackMv[idx, 1]) = (mv.RefStackMv[idx, 1], mv.RefStackMv[idx - 1, 1]);
                    newEnd = idx;
                }
            }

            end = newEnd;
        }
    }

    private static int ClampMvRow(int mvec, int border, int r, int bSize, int miRows)
    {
        int bh4 = Av1BlockTables.Num4x4BlocksHigh[bSize];
        int mbToTopEdge = -(r * 4 * 8);
        int mbToBottomEdge = (miRows - bh4 - r) * 4 * 8;
        return Math.Clamp(mvec, mbToTopEdge - border, mbToBottomEdge + border);
    }

    private static int ClampMvCol(int mvec, int border, int c, int bSize, int miCols)
    {
        int bw4 = Av1BlockTables.Num4x4BlocksWide[bSize];
        int mbToLeftEdge = -(c * 4 * 8);
        int mbToRightEdge = (miCols - bw4 - c) * 4 * 8;
        return Math.Clamp(mvec, mbToLeftEdge - border, mbToRightEdge + border);
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.ReadMv</c>.</summary>
    private static void WriteMv(TileState s, int mvRow, int mvCol, int predMvRow, int predMvCol)
    {
        int diffRow = mvRow - predMvRow;
        int diffCol = mvCol - predMvCol;
        int mvJoint = (diffRow != 0 ? 2 : 0) | (diffCol != 0 ? 1 : 0);

        s.Symbols.WriteSymbol(s.Cdf.MvJoint, mvJoint);
        if (mvJoint == MvJointHzvnz || mvJoint == MvJointHnzvnz)
        {
            WriteMvComponent(s, 0, diffRow);
        }

        if (mvJoint == MvJointHnzvz || mvJoint == MvJointHnzvnz)
        {
            WriteMvComponent(s, 1, diffCol);
        }
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.ReadMvComponent</c>.</summary>
    private static void WriteMvComponent(TileState s, int comp, int diff)
    {
        int sign = diff < 0 ? 1 : 0;
        int absMv = Math.Abs(diff);

        // absMv is always a positive multiple of 8 (force_integer_mv is unconditionally 1 -- see
        // Av1TileDecoder.ReadMvComponent's remarks): k = absMv/8 - 1 inverts read_mv_component's own
        // (class0_bit or mv_class+d) -> magnitude packing exactly (mag-1's low 3 bits are always the
        // forced 111 from mv_class0_fr/hp or mv_fr/hp, so dividing by 8 and subtracting 1 recovers the
        // class-selecting index cleanly).
        int k = (absMv / 8) - 1;
        int mvClass = k <= 1 ? MvClass0 : Av1CdfAdaptation.FloorLog2((uint)k);

        s.Symbols.WriteSymbol(s.Cdf.MvSign[comp], sign);
        s.Symbols.WriteSymbol(s.Cdf.MvClass[comp], mvClass);
        if (mvClass == MvClass0)
        {
            s.Symbols.WriteSymbol(s.Cdf.MvClass0Bit[comp], k);
        }
        else
        {
            int d = k - (1 << mvClass);
            for (int i = 0; i < mvClass; i++)
            {
                s.Symbols.WriteSymbol(s.Cdf.MvBit[comp][i], (d >> i) & 1);
            }
        }
    }

    /// <summary><c>get_palette_bsize_ctx</c> (spec §8.3.2's palette context derivation): <c>FloorLog2(pixel count) - FloorLog2(64)</c> -- 0 for an 8x8 leaf up to 6 for 64x64.</summary>
    private static int GetPaletteBsizeCtx(int bSize)
    {
        int numPels = Av1BlockTables.BlockWidth(bSize) * Av1BlockTables.BlockHeight(bSize);
        return Av1CdfAdaptation.FloorLog2((uint)numPels) - 6;
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.GetPaletteModeCtx</c>: count of {above, left} neighbors that themselves used a luma palette.</summary>
    private static int GetPaletteModeCtx(TileState s, int r, int c, bool availU, bool availL)
    {
        int ctx = 0;
        if (availU && s.PaletteSizesY[((r - 1) * s.MiCols) + c] > 0)
        {
            ctx++;
        }

        if (availL && s.PaletteSizesY[(r * s.MiCols) + c - 1] > 0)
        {
            ctx++;
        }

        return ctx;
    }

    /// <summary>
    /// The write-side count-only mirror of <c>Av1TileDecoder.GetPaletteCache</c>: how many "is this cache
    /// color used" bits <see cref="WritePaletteColorsY"/>/<see cref="WritePaletteColorsUv"/> must write
    /// before their literal colors (this method's caller always answers every one of them "no" -- see
    /// EncodeLeaf's remarks -- but the *count* itself is spec-normative, computed from
    /// the same frame-shared above/left neighbor palette state a real decoder independently derives, so it
    /// has to match exactly even though none of the actual cache values end up used).
    /// </summary>
    private static int GetPaletteCacheCount(TileState s, int plane, int r, int c, bool availU, bool availL)
    {
        bool aboveExcluded = r % 16 == 0;
        bool useAbove = availU && !aboveExcluded;
        bool useLeft = availL;

        int[] sizes = plane == 0 ? s.PaletteSizesY : s.PaletteSizesUV;
        int[] grid = plane == 0 ? s.PaletteColorsYGrid : s.PaletteColorsUGrid;

        int aboveN = 0, leftN = 0, aboveBase = 0, leftBase = 0;
        if (useAbove)
        {
            int idx = ((r - 1) * s.MiCols) + c;
            aboveN = sizes[idx];
            aboveBase = idx * 8;
        }

        if (useLeft)
        {
            int idx = (r * s.MiCols) + c - 1;
            leftN = sizes[idx];
            leftBase = idx * 8;
        }

        if (aboveN == 0 && leftN == 0)
        {
            return 0;
        }

        int n = 0;
        int aboveIdx = 0, leftIdx = 0;
        int last = 0;
        bool hasLast = false;
        while (aboveIdx < aboveN && leftIdx < leftN)
        {
            int vAbove = grid[aboveBase + aboveIdx];
            int vLeft = grid[leftBase + leftIdx];
            int v;
            if (vLeft < vAbove)
            {
                v = vLeft;
                leftIdx++;
            }
            else
            {
                v = vAbove;
                aboveIdx++;
                if (vLeft == vAbove)
                {
                    leftIdx++;
                }
            }

            if (!hasLast || v != last)
            {
                n++;
                last = v;
                hasLast = true;
            }
        }

        while (aboveIdx < aboveN)
        {
            int v = grid[aboveBase + aboveIdx++];
            if (!hasLast || v != last)
            {
                n++;
                last = v;
                hasLast = true;
            }
        }

        while (leftIdx < leftN)
        {
            int v = grid[leftBase + leftIdx++];
            if (!hasLast || v != last)
            {
                n++;
                last = v;
                hasLast = true;
            }
        }

        return n;
    }

    /// <summary>
    /// Collects this leaf's distinct luma sample values into <paramref name="colors"/> (ascending, caller-
    /// sized to at least 8), returning <see langword="false"/> without touching <paramref name="colors"/>
    /// when there are more than 8 -- or fewer than 2 (spec's own <c>PALETTE_MIN_SIZE</c>: a genuinely
    /// solid, single-color leaf has no valid palette size at all, since <c>palette_size_y_minus_2</c> can
    /// never encode "1 color", so this correctly falls back to the ordinary residual path instead, which
    /// already codes a solid leaf for free via all-zero coefficients).
    /// </summary>
    private static bool TryBuildYPalette(TileState s, int x, int y, int size, int[] colors, out int count)
    {
        Span<int> found = stackalloc int[9];
        int n = 0;
        for (int i = 0; i < size; i++)
        {
            int rowBase = ((y + i) * s.YWidth) + x;
            for (int j = 0; j < size; j++)
            {
                int v = s.SourceY[rowBase + j];
                bool exists = false;
                for (int k = 0; k < n; k++)
                {
                    if (found[k] == v)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    if (n == 8)
                    {
                        count = 0;
                        return false;
                    }

                    found[n] = v;
                    n++;
                }
            }
        }

        if (n < 2)
        {
            count = 0;
            return false;
        }

        InsertionSort(found[..n]);
        for (int i = 0; i < n; i++)
        {
            colors[i] = found[i];
        }

        count = n;
        return true;
    }

    /// <summary>
    /// Collects this leaf's distinct (U, V) sample pairs (chroma444-coordinate, matching luma 1:1 -- see
    /// TryBuildYPalette's own >8-distinct-values check) into <paramref name="uColors"/>/<paramref name="vColors"/>
    /// (ascending by U, caller-sized to at least 8), returning <see langword="false"/> when there are more
    /// than 8 distinct pairs -- or fewer than 2, per spec's own <c>PALETTE_MIN_SIZE</c> (see
    /// <see cref="TryBuildYPalette"/>'s identical remark).
    /// </summary>
    private static bool TryBuildUvPalette(TileState s, int x, int y, int size, int[] uColors, int[] vColors, out int count)
    {
        Span<int> foundU = stackalloc int[9];
        Span<int> foundV = stackalloc int[9];
        int n = 0;
        for (int i = 0; i < size; i++)
        {
            int rowBase = ((y + i) * s.ChromaWidth) + x;
            for (int j = 0; j < size; j++)
            {
                int u = s.SourceU![rowBase + j];
                int v = s.SourceV![rowBase + j];
                bool exists = false;
                for (int k = 0; k < n; k++)
                {
                    if (foundU[k] == u && foundV[k] == v)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    if (n == 8)
                    {
                        count = 0;
                        return false;
                    }

                    foundU[n] = u;
                    foundV[n] = v;
                    n++;
                }
            }
        }

        if (n < 2)
        {
            count = 0;
            return false;
        }

        for (int i = 1; i < n; i++)
        {
            int keyU = foundU[i];
            int keyV = foundV[i];
            int j = i - 1;
            while (j >= 0 && foundU[j] > keyU)
            {
                foundU[j + 1] = foundU[j];
                foundV[j + 1] = foundV[j];
                j--;
            }

            foundU[j + 1] = keyU;
            foundV[j + 1] = keyV;
        }

        for (int i = 0; i < n; i++)
        {
            uColors[i] = foundU[i];
            vColors[i] = foundV[i];
        }

        count = n;
        return true;
    }

    private static void InsertionSort(Span<int> values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            int key = values[i];
            int j = i - 1;
            while (j >= 0 && values[j] > key)
            {
                values[j + 1] = values[j];
                j--;
            }

            values[j + 1] = key;
        }
    }

    /// <summary>
    /// Write-side mirror of <c>Av1TileDecoder.ReadPaletteColorsY</c>: writes <see cref="GetPaletteCacheCount"/>
    /// "not cached" bits, then every color explicitly (the first as a raw 8-bit literal, the rest as
    /// ascending <c>delta - 1</c> values). The delta bit-width is <em>not</em> a free encoder choice held
    /// constant across every delta -- the decoder recomputes it after every color from the shrinking
    /// "remaining value range" (<c>range -= colors[idx] - colors[idx - 1]</c>, then
    /// <c>bits = min(bits, CeilLog2(range))</c>), so this has to replicate that exact shrinking, not just
    /// pick one width generously large enough for the first delta and reuse it -- getting this wrong doesn't
    /// throw or produce an out-of-range value, it just silently reads the wrong number of bits for every
    /// delta after the first mismatch, corrupting everything downstream in the tile (a bug that measured
    /// zero effect on an all-equal-colors test case, where every "wrong-width" read still decoded to zero by
    /// coincidence, and only became visible on genuinely varying colors).
    /// </summary>
    private static void WritePaletteColorsY(TileState s, int[] colors, int n, int r, int c, bool availU, bool availL)
    {
        int nCache = GetPaletteCacheCount(s, 0, r, c, availU, availL);
        for (int i = 0; i < nCache; i++)
        {
            s.Symbols.WriteLiteral(0, 1);
        }

        s.Symbols.WriteLiteral((uint)colors[0], 8);
        if (n > 1)
        {
            const int minBits = 5; // bitDepth(8) - 3
            const int extraBits = 3; // always the maximum -- see minBits/extraBits's remarks on why this is always safe as the *starting* width; it only ever shrinks from here, identically on both sides.
            s.Symbols.WriteLiteral(extraBits, 2);
            int bits = minBits + extraBits;
            int range = 256 - colors[0] - 1;
            for (int idx = 1; idx < n; idx++)
            {
                s.Symbols.WriteLiteral((uint)(colors[idx] - colors[idx - 1] - 1), bits);
                range -= colors[idx] - colors[idx - 1];
                bits = Math.Min(bits, Av1TileDecoder.CeilLog2(range));
            }
        }
    }

    /// <summary>
    /// Write-side mirror of <c>Av1TileDecoder.ReadPaletteColorsUv</c>: U follows <see cref="WritePaletteColorsY"/>'s
    /// exact shape -- including its adaptive delta bit-width shrinking, against the U-specific cache count
    /// and U's own (unshifted by the <c>-1</c>) range formula -- see <see cref="WritePaletteColorsY"/>'s
    /// remarks for why this shrinking isn't optional. V is always written in its non-delta form (spec's
    /// <c>palette_colors_v_contain_extra_bit</c>-style bit = 0), a flat 8-bit literal per color in
    /// palette-index order -- simpler and always correct, unlike the delta form, which would need
    /// modular-wraparound reasoning to guarantee every delta fits its bit budget.
    /// </summary>
    private static void WritePaletteColorsUv(TileState s, int[] uColors, int[] vColors, int n, int r, int c, bool availU, bool availL)
    {
        int nCache = GetPaletteCacheCount(s, 1, r, c, availU, availL);
        for (int i = 0; i < nCache; i++)
        {
            s.Symbols.WriteLiteral(0, 1);
        }

        s.Symbols.WriteLiteral((uint)uColors[0], 8);
        if (n > 1)
        {
            const int minBits = 5; // bitDepth(8) - 3
            const int extraBits = 3;
            s.Symbols.WriteLiteral(extraBits, 2);
            int bits = minBits + extraBits;
            int range = 256 - uColors[0]; // U's range omits Y's "- 1" -- matches Av1TileDecoder.ReadPaletteColorsUv's own U-branch formula exactly.
            for (int idx = 1; idx < n; idx++)
            {
                s.Symbols.WriteLiteral((uint)(uColors[idx] - uColors[idx - 1]), bits);
                range -= uColors[idx] - uColors[idx - 1];
                bits = Math.Min(bits, Av1TileDecoder.CeilLog2(range));
            }
        }

        s.Symbols.WriteBool(0); // not delta-encoded
        for (int i = 0; i < n; i++)
        {
            s.Symbols.WriteLiteral((uint)vColors[i], 8);
        }
    }

    /// <summary>Fills <paramref name="colors"/>[i]-index for every pixel in this leaf's luma region, row-major, stride <paramref name="size"/>.</summary>
    private static void BuildColorMap(int[] source, int stride, int x, int y, int size, int[] colors, int n, int[] outMap)
    {
        for (int i = 0; i < size; i++)
        {
            int rowBase = ((y + i) * stride) + x;
            int outRowBase = i * size;
            for (int j = 0; j < size; j++)
            {
                int v = source[rowBase + j];
                int idx = 0;
                for (int k = 0; k < n; k++)
                {
                    if (colors[k] == v)
                    {
                        idx = k;
                        break;
                    }
                }

                outMap[outRowBase + j] = idx;
            }
        }
    }

    /// <summary>Fills the shared UV color-index map for every pixel in this leaf's chroma444 region, row-major, stride <paramref name="size"/> -- one index per (U, V) pair, matching <see cref="TryBuildUvPalette"/>'s own pairing.</summary>
    private static void BuildColorMapUv(TileState s, int x, int y, int size, int[] uColors, int[] vColors, int n, int[] outMap)
    {
        for (int i = 0; i < size; i++)
        {
            int rowBase = ((y + i) * s.ChromaWidth) + x;
            int outRowBase = i * size;
            for (int j = 0; j < size; j++)
            {
                int u = s.SourceU![rowBase + j];
                int v = s.SourceV![rowBase + j];
                int idx = 0;
                for (int k = 0; k < n; k++)
                {
                    if (uColors[k] == u && vColors[k] == v)
                    {
                        idx = k;
                        break;
                    }
                }

                outMap[outRowBase + j] = idx;
            }
        }
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.DecodeColorMapTokens</c>, restricted to this encoder's always-fully-on-screen leaves (no off-screen edge extension needed -- see the class-level remarks on padding). Writes the first index via <see cref="Av1SymbolEncoder.WriteNs"/>, then every later position in the same anti-diagonal ("wavefront") order the decoder reads in, so <see cref="Av1TileDecoder.GetPaletteColorIndexContext"/>'s left/above-left/above neighbors are always already-written -- reusing that exact method (rather than a separate write-side copy) guarantees the context this writes against can never drift from what a real decoder derives.</summary>
    private static void WriteColorMapTokens(TileState s, int[] colorMap, int size, int n, ushort[][][] mapCdf)
    {
        var colorOrder = new int[8];
        var inverseColorOrder = new int[8];

        s.Symbols.WriteNs(colorMap[0], n);

        for (int i = 1; i < (2 * size) - 1; i++)
        {
            for (int j = Math.Min(i, size - 1); j >= Math.Max(0, i - size + 1); j--)
            {
                int row = i - j;
                int col = j;
                int ctx = Av1TileDecoder.GetPaletteColorIndexContext(colorMap, size, row, col, n, colorOrder);
                for (int k = 0; k < n; k++)
                {
                    inverseColorOrder[colorOrder[k]] = k;
                }

                int trueIdx = colorMap[(row * size) + col];
                int symbol = inverseColorOrder[trueIdx];
                s.Symbols.WriteSymbol(mapCdf[n - 2][ctx], symbol);
            }
        }
    }

    /// <summary>
    /// Encodes the chroma (U then V, matching spec <c>residual()</c>'s plane-outer loop order -- see the
    /// remarks below) region matching one luma leaf: at 4:2:0, a <c>(sizeMi/2)</c>-square grid of 4x4
    /// sub-blocks (half the luma leaf's mi extent in each dimension); at 4:4:4 (only ever paired with
    /// <see cref="TileState.Lossless"/> -- see <see cref="Av1FrameEncoder.Encode"/>'s <c>chroma444</c> gate),
    /// a <c>sizeMi</c>-square grid at luma-identical (unhalved) coordinates. Always DC_PRED (CFL isn't
    /// implemented, so chroma mode never varies), and follows <see cref="TileState.Lossless"/> per sub-block
    /// for WHT vs. DCT_DCT exactly like the single-sub-block case this generalizes did.
    ///
    /// <para>Plane must be the outer loop and sub-block position the inner loop -- not the reverse -- to
    /// match spec §5.11.34 <c>residual()</c>'s own <c>for (plane ...) { for (y...) for (x...)
    /// transform_block() } }</c> nesting: a real decoder reads every one of U's transform blocks in this
    /// coding block before reading any of V's, so writing them interleaved by position would silently
    /// desync the entropy stream the moment there's more than one sub-block per plane (any leaf bigger than
    /// the previous fixed 8x8 grid).</para>
    /// </summary>
    private static void EncodeChromaRegion(TileState s, int r, int c, int x, int y, int sizeMi)
    {
        int chromaN = s.Chroma444 ? sizeMi : sizeMi / 2;
        int chromaR4Base = s.Chroma444 ? r : r / 2;
        int chromaC4Base = s.Chroma444 ? c : c / 2;
        int cxBase = s.Chroma444 ? x : x / 2;
        int cyBase = s.Chroma444 ? y : y / 2;

        // The chroma coding block's own pixel width/height, passed to WriteCoeffs as blockSize whenever it
        // differs from the 4x4 transform (chromaN > 1) -- matches EncodeLosslessLumaResidual's identical
        // blockSize reasoning, just applied to the (possibly smaller, 4:2:0) chroma region instead of luma's.
        int chromaBlockSizePixels = chromaN * 4;
        int blockSizeArg = chromaN > 1 ? chromaBlockSizePixels : 0;

        foreach (var (source, recon, ctx) in new[]
        {
            (s.SourceU!, s.ReconU!, s.UCoeffCtx!),
            (s.SourceV!, s.ReconV!, s.VCoeffCtx!),
        })
        {
            for (int dr = 0; dr < chromaN; dr++)
            {
                for (int dc = 0; dc < chromaN; dc++)
                {
                    int chromaR4 = chromaR4Base + dr;
                    int chromaC4 = chromaC4Base + dc;
                    int cx = cxBase + (dc * 4);
                    int cy = cyBase + (dr * 4);
                    bool availU = chromaR4 > 0;
                    bool availL = chromaC4 > 0;

                    var above = new Av1EdgeArray(16);
                    var left = new Av1EdgeArray(16);
                    Av1IntraPrediction.BuildEdges(above, left, recon, s.ChromaWidth, cx, cy, 4, 4, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);

                    // Reuses the same tile-wide scratch buffers luma just finished using for this leaf --
                    // safe because luma's use of them is already fully consumed (WriteCoeffs/Reconstruct
                    // called for every luma sub-block) before this method runs.
                    var pred = s.Pred;
                    Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, above, left, Av1IntraMode.DcPred, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta: 0, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.ChromaWidth - 1, s.ChromaHeight - 1, cx, cy, bitDepth: 8);

                    var residual = s.Residual;
                    for (int i = 0; i < 16; i++)
                    {
                        residual[i] = source[((cy + (i / 4)) * s.ChromaWidth) + cx + (i % 4)] - pred[i];
                    }

                    var coeff = s.Coeff;
                    if (s.Lossless)
                    {
                        Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                    }
                    else
                    {
                        Av1ForwardTransform.Forward2D(residual.AsSpan(0, 16), coeff.AsSpan(0, 16), 4);
                    }

                    var levels = s.Levels;
                    Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                    for (int i = 0; i < 4; i++)
                    {
                        Array.Copy(pred, i * 4, recon, ((cy + i) * s.ChromaWidth) + cx, 4);
                    }

                    // (x4, y4) = (chromaC4, chromaR4), not (chromaR4, chromaC4) -- see EncodeLeaf's luma call
                    // site for why the argument order matters here (x4 = column, y4 = row) even though it's
                    // unobservable on any square/single-superblock chroma grid.
                    Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 1, chromaC4, chromaR4, ctx, writeLumaTxType: null, blockSize: blockSizeArg);
                    Av1LocalReconstructor.Reconstruct(recon, s.ChromaWidth, cx, cy, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, s.Lossless);
                }
            }
        }
    }

    /// <summary>
    /// Encodes a leaf's luma residual when lossless: AV1 forces <c>TX_4X4</c> for every block at
    /// coded-lossless, so unlike the non-lossless path (one whole-leaf DCT_DCT transform over a single
    /// whole-block prediction, only ever an 8x8 leaf here), this predicts, transforms, and reconstructs each
    /// of the <c>(blockSize/4)^2</c> 4x4 sub-blocks in raster order individually -- later sub-blocks'
    /// predictions read the just-reconstructed pixels of earlier sub-blocks in this same coding block as
    /// edge context (spec <c>predict_intra()</c> operates at the transform-block level, not the coding-block
    /// level, whenever <c>TxSize &lt; block size</c>), using <paramref name="bestMode"/> (chosen once for the
    /// whole leaf, via the SSE search in <see cref="EncodeLeaf"/> -- the encoder is free to use whatever mode-
    /// decision heuristic it likes, only the actual prediction executed here has to be spec-correct) instead
    /// of always DC_PRED, and <see cref="Av1ForwardWht"/> instead of DCT. <c>tx_type</c> is never signalled
    /// here (matching <c>Av1TileDecoder.TransformType</c>'s own <c>qindex &lt;= 0</c> short-circuit, which
    /// never reads a tx_type symbol at coded-lossless either).
    /// </summary>
    private static void EncodeLosslessLumaResidual(TileState s, int r, int c, int x, int y, int bestMode, int angleDelta, bool filterTypeSmooth, bool useFilterIntra, int filterIntraMode, int blockSize)
    {
        int n = blockSize / 4;
        for (int dr = 0; dr < n; dr++)
        {
            for (int dc = 0; dc < n; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                int subR = r + dr;
                int subC = c + dc;
                bool availU = subR > 0;
                bool availL = subC > 0;

                // haveAboveRight/haveBelowLeft from the real BlockDecoded state (spec-accurate, mirroring
                // Av1TileDecoder.TransformBlock exactly at this same 4x4-transform-block granularity) --
                // required for correctness, not just search quality, once angle_delta is actually nonzero:
                // see EncodeLeaf's identical remarks on this same point for the whole-block search buffer.
                int subBlockMiRow = subR & 15;
                int subBlockMiCol = subC & 15;
                bool haveAboveRight = GetBlockDecoded(s, 0, subBlockMiRow - 1, subBlockMiCol + 1);
                bool haveBelowLeft = GetBlockDecoded(s, 0, subBlockMiRow + 1, subBlockMiCol - 1);

                var above = new Av1EdgeArray(16);
                var left = new Av1EdgeArray(16);
                Av1IntraPrediction.BuildEdges(above, left, s.ReconY, s.YWidth, subX, subY, 4, 4, availL, availU, haveAboveRight, haveBelowLeft, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);

                var pred = s.Pred;
                Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, above, left, bestMode, availL, availU, useFilterIntra, filterIntraMode, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.YWidth - 1, s.YHeight - 1, subX, subY, bitDepth: 8);

                var residual = s.Residual;
                for (int i = 0; i < 16; i++)
                {
                    residual[i] = s.SourceY[((subY + (i / 4)) * s.YWidth) + subX + (i % 4)] - pred[i];
                }

                var coeff = s.Coeff;
                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                var levels = s.Levels;
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                for (int i = 0; i < 4; i++)
                {
                    Array.Copy(pred, i * 4, s.ReconY, ((subY + i) * s.YWidth) + subX, 4);
                }

                // blockSize: the coding block's real pixel size -- always > 4 for every leaf this encoder
                // produces (the floor is 8x8), so the all_zero context can't take the transform-equals-block
                // shortcut (see WriteCoeffs's remarks).
                Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 0, subC, subR, s.YCoeffCtx, writeLumaTxType: null, blockSize: blockSize);
                Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, subX, subY, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);
                SetBlockDecoded(s, 0, subBlockMiRow, subBlockMiCol, true);
            }
        }
    }
}
