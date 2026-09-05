using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Internal;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Encodes one single-tile intra frame: walks the superblock grid (spec §5.11.4 <c>decode_partition()</c>'s
/// write-side mirror). Non-lossless frames still force every 64x64 superblock to split all the way down to
/// a uniform 8x8 leaf grid (this encoder does not implement non-lossless partition-tree RDO -- see
/// <see cref="EncodePartitionForced"/>'s remarks for why). Lossless frames use 128x128 superblocks instead
/// (<c>use_128x128_superblock</c>, matching libaom's own default choice for non-tiny images -- see
/// <c>Av1SequenceHeaderWriter</c>) and run a real (if approximate) rate-distortion partition search
/// (<see cref="DecidePartition"/>, Phase D) at each partition level down to spec's true 4x4 floor, comparing
/// the actual per-coefficient rate cost (<see cref="ComputeCandidateCost"/>) of keeping an
/// 8x8/16x16/32x32/64x64/128x128 region as one leaf against the summed cost of its 4 quadrants, rather
/// than a pure flatness/variance threshold -- merging above 4x4 only ever reduces per-leaf mode/skip/partition
/// signaling, never residual coefficient cost by itself (AV1 forces TX_4X4 for every lossless block regardless
/// of coding-block size, so the same number of 4x4 Walsh-Hadamard sub-blocks get coded either way), but a real
/// cost comparison (unlike the pure-flatness heuristic it replaced) can also correctly choose *not* to merge
/// when a coarser single-mode prediction across the merged region would cost more in residual than it saves
/// in signaling -- see <see cref="DecidePartition"/>'s remarks for the full reasoning and the project plan's
/// Phase A/D results for the measurements motivating this. Every leaf gets a real, rate-cost-based intra mode
/// search (13 candidate modes x 7 angle_delta values for the 8 directional ones -- forced to just angle_delta
/// 0 for a 4x4 leaf, spec's own floor for signaling angle_delta at all, see <see cref="EncodeLeaf"/>'s
/// <c>angleDeltaAllowed</c> remarks -- plus 5 FILTER_INTRA candidates when DC_PRED wins -- see
/// <see cref="EncodeLeaf"/>). Chroma gets the same real directional/angle search too
/// (<see cref="SearchUvMode"/>), for both lossless and non-lossless: non-lossless chroma's transform type is
/// mode-dependent (<c>Av1TxTypeTables.ModeToTxfm</c>), so <see cref="EncodeChromaRegion"/> forward-transforms
/// with the matching DCT/ADST-mixed <c>Av1ForwardTransform</c> operator for whatever <c>uv_mode</c> the search
/// picks, rather than always DCT -- see that method's remarks. CFL (<see cref="TryCflCandidate"/>) is searched
/// for non-lossless chroma; spec-illegal for this encoder's lossless-always-4:4:4 RGB output, so never
/// attempted there.
///
/// <para>Requires the luma plane's width/height to already be padded to a multiple of the real superblock
/// size -- 128 for lossless, 64 otherwise (the caller's job -- see <c>Av1FrameEncoder</c>) -- so every
/// superblock is a full, in-bounds block: this eliminates every one of <c>decode_partition()</c>'s
/// edge-of-frame special cases (the <c>hasRows</c>/<c>hasCols</c>-driven HORZ/VERT-forced partitions), which
/// this encoder does not implement.</para>
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
            SbMiMask = lossless ? 31 : 15,
            Cdf = cdf,
            Symbols = symbols,
            YModes = new int[miCols * miRows],
            UvModes = new int[miCols * miRows],
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
            // 128*128, not 64*64: lossless's real leaf ceiling is now a full 128x128 superblock (see
            // Av1TileEncoder's own remarks on 128x128 superblocks), and a palette leaf can be that big.
            PaletteColorMap = AvifBufferPool.SharedInt32.Rent(128 * 128),
            YCoeffCtx = new Av1CoefficientWriter.PlaneContext(miCols, miRows),
            UCoeffCtx = monoChrome ? null : new Av1CoefficientWriter.PlaneContext(chroma444 ? miCols : miCols / 2, chroma444 ? miRows : miRows / 2),
            VCoeffCtx = monoChrome ? null : new Av1CoefficientWriter.PlaneContext(chroma444 ? miCols : miCols / 2, chroma444 ? miRows : miRows / 2),

            // Sized like YCoeffCtx (the largest of Y/U/V, since chroma's x4/y4 range is always a subset of
            // luma's numeric range -- true even at 4:4:4, where they're equal) so one shared, reused scratch
            // buffer safely backs ComputeCandidateCost's trial costing for every plane -- see
            // Av1CoefficientWriter.PlaneContext.SeedFrom's remarks.
            ScratchCoeffCtx = new Av1CoefficientWriter.PlaneContext(miCols, miRows),
            TrialSink = new Av1TrialSymbolSink(),
            Lambda = Av1RdCost.QIndexToLambda(baseQIdx),

            // Rented once for the whole tile and reused/overwritten across every block below, rather than
            // allocated fresh per block. Pred/BestPred must be sized for the largest leaf this encoder can
            // now produce (128x128 = 16384 elements, lossless only -- a full 128x128 superblock kept as one
            // leaf, see EncodeTile's own remarks on 128x128 superblocks): the whole-leaf mode search predicts
            // into them at the leaf's real size before any residual coding happens.
            // Residual/Coeff/Levels/ReconResidual only ever hold one transform block's worth of data at a
            // time -- 1024 elements covers this encoder's largest single transform (a non-lossless 32x32
            // DCT_DCT leaf, see EncodePartitionForced/EncodeLeaf's partition/TX-size RDO remarks; also enough
            // for the largest non-lossless chroma region a 32x32 luma leaf produces at 4:2:0, 16x16 = 256) --
            // or 16 for any lossless 4x4 WHT sub-block, regardless of how big the coding block containing it
            // is. ReconDequant alone stays fixed at 64*64 regardless of block size -- see
            // Av1LocalReconstructor.Reconstruct's remarks on why that stride can't shrink.
            Pred = AvifBufferPool.SharedInt32.Rent(128 * 128),
            BestPred = AvifBufferPool.SharedInt32.Rent(128 * 128),
            Residual = AvifBufferPool.SharedInt32.Rent(32 * 32),
            Coeff = AvifBufferPool.SharedInt32.Rent(32 * 32),
            Levels = AvifBufferPool.SharedInt32.Rent(32 * 32),
            ReconDequant = AvifBufferPool.SharedInt32.Rent(64 * 64),
            ReconResidual = AvifBufferPool.SharedInt32.Rent(32 * 32),

            // Separate from Residual: TryCflPlane's alpha-candidate loop needs this to stay stable across
            // multiple ComputeCandidateCost calls, but ComputeCandidateCost's own non-lossless branch
            // clobbers TileState.Residual as its own scratch on every call -- aliasing the two would silently
            // feed ApplyCflAlpha the previous candidate's leftover pixel residual instead of real luma AC
            // data from the second alpha candidate onward.
            CflLumaAc = AvifBufferPool.SharedInt32.Rent(32 * 32),

            // Non-lossless luma's real, final quantized levels, computed and cached before SearchUvMode runs
            // (so CFL's search has this leaf's own real reconstructed luma to work with) and consumed later
            // at the leaf's normal bitstream-order commit position. Separate from TileState.Levels: that
            // buffer is shared, trial-only scratch that ComputeCandidateCost's own non-lossless branch
            // (called many times over, by SearchUvMode's mode loop and CFL alike) clobbers on every call.
            LumaLevels = AvifBufferPool.SharedInt32.Rent(32 * 32),
        };

        try
        {
            // Superblock size: 128x128 (sizeMi 32) for lossless, matching Av1SequenceHeaderWriter's
            // use_128x128_superblock signaling (always exactly lossless -- see its own remarks) and
            // Av1FrameEncoder's matching 128-pixel-multiple padding; 64x64 (sizeMi 16) otherwise, this
            // encoder's original, still-current non-lossless configuration.
            int sbSizeMi = lossless ? 32 : 16;
            for (int r = 0; r < miRows; r += sbSizeMi)
            {
                for (int c = 0; c < miCols; c += sbSizeMi)
                {
                    ClearBlockDecodedFlags(state, r, c, sbSize4: sbSizeMi);
                    EncodePartitionForced(state, r, c, sizeMi: sbSizeMi);
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
            AvifBufferPool.SharedInt32.Return(state.CflLumaAc);
            AvifBufferPool.SharedInt32.Return(state.LumaLevels);
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

        /// <summary>
        /// Mi-unit mask for converting an absolute mi row/col into its position relative to the current
        /// top-level superblock (<c>r &amp; SbMiMask</c>) -- 31 (128x128, 32 mi units) when this frame uses
        /// 128x128 superblocks (always exactly <see cref="Lossless"/>, see <c>Av1TileEncoder.EncodeTile</c>'s
        /// top-level loop), 15 (64x64, 16 mi units) otherwise. <see cref="BlockDecodedStride"/> is already
        /// sized generously enough for either case.
        /// </summary>
        public required int SbMiMask;
        public required Av1CdfContext Cdf;
        public required Av1SymbolEncoder Symbols;
        public required int[] YModes;
        public required int[] UvModes;
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
        public required Dictionary<(int R, int C, int SizeMi), (int Type, long Cost)> PartitionDecisions;
        public required Av1CoefficientWriter.PlaneContext YCoeffCtx;
        public required Av1CoefficientWriter.PlaneContext? UCoeffCtx;
        public required Av1CoefficientWriter.PlaneContext? VCoeffCtx;

        // RD cost search scratch (Av1RdCost / ComputeCandidateCost): ScratchCoeffCtx is reseeded from the
        // real Y/U/V context before every candidate's trial cost, then freely mutated (never written back)
        // by that one candidate's own WriteCoeffs trial calls -- see PlaneContext.SeedFrom's remarks. TrialSink
        // is the one reused Av1TrialSymbolSink every trial WriteCoeffs call accumulates into (Reset() between
        // candidates), avoiding a fresh allocation on every one of a leaf's ~100+ candidate evaluations.
        // Lambda is this frame's qindex-derived RD weight (Av1RdCost.QIndexToLambda), computed once here
        // rather than per candidate.
        public required Av1CoefficientWriter.PlaneContext ScratchCoeffCtx;
        public required Av1TrialSymbolSink TrialSink;
        public required double Lambda;

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
        public required int[] CflLumaAc;
        public required int[] LumaLevels;
    }

    private static int BlockSizeFromSizeMi(int sizeMi) => sizeMi switch
    {
        32 => Av1BlockSize.Block128x128,
        16 => Av1BlockSize.Block64x64,
        8 => Av1BlockSize.Block32x32,
        4 => Av1BlockSize.Block16x16,
        2 => Av1BlockSize.Block8x8,
        _ => Av1BlockSize.Block4x4,
    };

    /// <summary>
    /// Rectangular counterpart to <see cref="BlockSizeFromSizeMi"/>, for a HORZ/VERT-split leaf (<paramref
    /// name="wMi"/> != <paramref name="hMi"/>) -- falls back to the square mapping when they're equal.
    /// Covers exactly the shapes <see cref="ComputeDecidePartition"/>'s Horz/Vert candidates can produce
    /// (parents up to 64x64, see its own remarks): 8x4/4x8 up to 64x32/32x64.
    /// </summary>
    private static int BlockSizeFromWidthHeightMi(int wMi, int hMi) => (wMi, hMi) switch
    {
        (2, 1) => Av1BlockSize.Block8x4,
        (1, 2) => Av1BlockSize.Block4x8,
        (4, 2) => Av1BlockSize.Block16x8,
        (2, 4) => Av1BlockSize.Block8x16,
        (8, 4) => Av1BlockSize.Block32x16,
        (4, 8) => Av1BlockSize.Block16x32,
        (16, 8) => Av1BlockSize.Block64x32,
        (8, 16) => Av1BlockSize.Block32x64,
        _ => BlockSizeFromSizeMi(wMi),
    };

    /// <summary><c>log2</c> of a leaf's pixel width/height, for <see cref="Av1IntraPrediction.Predict"/>'s <c>log2W</c>/<c>log2H</c> parameters -- e.g. sizeMi 16 (64 pixels) -&gt; 6.</summary>
    private static int PixelLog2(int sizeMi) => sizeMi switch
    {
        32 => 7,
        16 => 6,
        8 => 5,
        4 => 4,
        2 => 3,
        _ => 2,
    };

    private static void EncodePartitionForced(TileState s, int r, int c, int sizeMi)
    {
        // decode_partition() never reads a partition symbol below 8x8 (spec §5.11.4: the read is gated on
        // bSize >= BLOCK_8X8) -- a 4x4 node is always a leaf, unconditionally, with no signaling of its own.
        // Only reachable at all when lossless (see the sizeMi == 2 case below): non-lossless never recurses
        // this far since it never calls DecidePartition.
        if (sizeMi == 1)
        {
            EncodeLeaf(s, r, c, sizeMi);
            return;
        }

        int bSize = BlockSizeFromSizeMi(sizeMi);

        // decode_partition() reads a partition symbol at every size down to and including 8x8 (only sizes
        // *below* 8x8 skip it, see the sizeMi == 1 case above).
        int ctx = PartitionContext(s, r, c, bSize, out int bsl);
        var partitionCdf = bsl switch
        {
            1 => s.Cdf.PartitionW8[ctx],
            2 => s.Cdf.PartitionW16[ctx],
            3 => s.Cdf.PartitionW32[ctx],
            4 => s.Cdf.PartitionW64[ctx],
            _ => s.Cdf.PartitionW128[ctx],
        };

        // Real RD partition search (DecidePartition, Phase D technique 6 originally, now live for
        // non-lossless too -- see the project plan's partition/TX-size RDO phase) picks split-vs-leaf at
        // every level down to each mode's own floor: lossless reaches spec's true 4x4 floor (sizeMi == 1,
        // see above) since DecidePartition's own remarks explain why going smaller matters for hard-edged/
        // screen-content-style graphics; non-lossless floors at sizeMi == 2 (8x8) instead -- DecidePartition
        // itself never recurses non-lossless below that (see its own sizeMi == 2 remarks), so this call
        // always terminates correctly without a separate forced-8x8 special case here anymore. This includes
        // leaves that end up using IntraBC's approximate-match residual path (EncodeIntrabcResidual, lossless
        // only), which predicts every plane fresh per 4x4 sub-block from progressively-reconstructed state,
        // matching Av1TileDecoder.TransformBlock's own per-sub-block PredictIntrabc call (spec §5.11.35) for
        // any leaf size -- see EncodeIntrabcResidual's remarks. That path used to be gated to single-sub-block
        // (sizeMi <= 2) leaves specifically because it predicted a merged coding block in one whole-block
        // PredictIntrabc call instead, which could desync from a real decoder's per-sub-block prediction for
        // a genuinely multi-sub-block IntraBC block; IntraBC's *exact*-match path (skip = 1, no residual,
        // verified byte-identical to source before use) never had this problem, since it carries no
        // per-sub-block prediction step at all.
        int decidedType = DecidePartition(s, r, c, sizeMi).Type;
        int half = sizeMi / 2;

        switch (decidedType)
        {
            case Av1PartitionType.None:
                s.Symbols.WriteSymbol(partitionCdf, Av1PartitionType.None);
                EncodeLeaf(s, r, c, sizeMi);
                return;

            // HORZ/VERT (first increment of full AV1 partition-type support, lossless only -- see
            // ComputeDecidePartition's own remarks): each produces exactly two final, non-recursing leaves
            // (spec's partition_subsize() gives the final block size directly), unlike SPLIT below.
            case Av1PartitionType.Horz:
                s.Symbols.WriteSymbol(partitionCdf, Av1PartitionType.Horz);
                EncodeRectangularLeaf(s, r, c, sizeMi, half);
                EncodeRectangularLeaf(s, r + half, c, sizeMi, half);
                return;

            case Av1PartitionType.Vert:
                s.Symbols.WriteSymbol(partitionCdf, Av1PartitionType.Vert);
                EncodeRectangularLeaf(s, r, c, half, sizeMi);
                EncodeRectangularLeaf(s, r, c + half, half, sizeMi);
                return;

            default:
                s.Symbols.WriteSymbol(partitionCdf, Av1PartitionType.Split);
                EncodePartitionForced(s, r, c, half);
                EncodePartitionForced(s, r, c + half, half);
                EncodePartitionForced(s, r + half, c, half);
                EncodePartitionForced(s, r + half, c + half, half);
                return;
        }
    }

    // A split node's own signaling is now priced exactly (the real partition symbol's bit cost, computed in
    // DecidePartition itself via Av1SymbolEncoder.EstimateSymbolCost against the real, context-selected
    // partition CDF -- see PartitionContext) rather than this flat stand-in. A leaf's *partition* symbol is
    // priced the same exact way, but a leaf also pays signaling this cost function can't know yet at
    // DecidePartition time, before EncodeLeaf's own mode search has run: one skip bit, one yMode symbol,
    // (sometimes) an angle_delta, a uv_mode, and palette/filter-intra eligibility bits. This remaining
    // constant stands in for just that piece now (smaller than the old LeafSignalingCost/SplitSignalingCost
    // pair's combined 24, since the partition symbol itself no longer needs to be approximated inside it) --
    // tuned as a relative weight, not a calibrated bit count, same as before; pricing this piece for real too
    // (e.g. by running a cheap mode-search pass before the partition decision) is a candidate for a later RD
    // phase, not this one.
    private const long LeafOtherSignalingCost = 16;

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
    private static (int Type, long Cost) DecidePartition(TileState s, int r, int c, int sizeMi)
    {
        if (s.PartitionDecisions.TryGetValue((r, c, sizeMi), out var cached))
        {
            return cached;
        }

        var result = ComputeDecidePartition(s, r, c, sizeMi);
        s.PartitionDecisions[(r, c, sizeMi)] = result;
        return result;
    }

    /// <summary>Actual decision logic behind <see cref="DecidePartition"/>, split out so every return path still goes through that method's single memoization write -- see its own remarks for the overall approach.</summary>
    private static (int Type, long Cost) ComputeDecidePartition(TileState s, int r, int c, int sizeMi)
    {
        // Spec's true 4x4 partition floor (see EncodePartitionForced's sizeMi == 1 case) -- lossless is the
        // only mode that ever reaches this (non-lossless's own floor is sizeMi == 2, below) -- nothing
        // smaller to compare against, and no partition symbol is read/written at this size at all (spec
        // §5.11.4 gates the read on bSize >= BLOCK_8X8), so unlike every case below, no partition-symbol bit
        // cost applies here either.
        if (sizeMi == 1)
        {
            return (Av1PartitionType.None, EstimateLumaCost(s, r, c, sizeMi) + LeafOtherSignalingCost);
        }

        // Real partition-symbol bit costs (Av1SymbolEncoder.EstimateSymbolCost), replacing the old flat
        // LeafSignalingCost/SplitSignalingCost stand-ins for this component specifically -- computed against
        // the same context-selected CDF EncodePartitionForced's real write uses for this same (r, c, sizeMi)
        // node (see PartitionContext), so unlike those old flat constants this actually reflects how skewed
        // this position's real, already-adapted partition CDF is (a region whose neighbors were mostly split
        // costs more to signal PARTITION_NONE here than one whose neighbors mostly weren't, and vice versa
        // for PARTITION_SPLIT) instead of charging every node the same amount regardless of context.
        int bSize = BlockSizeFromSizeMi(sizeMi);
        int ctx = PartitionContext(s, r, c, bSize, out int bsl);
        var partitionCdf = bsl switch
        {
            1 => s.Cdf.PartitionW8[ctx],
            2 => s.Cdf.PartitionW16[ctx],
            3 => s.Cdf.PartitionW32[ctx],
            4 => s.Cdf.PartitionW64[ctx],
            _ => s.Cdf.PartitionW128[ctx],
        };

        // Signaling-bit terms (noneBits/splitBits/LeafOtherSignalingCost) are real bit *counts* -- for
        // lossless, EstimateLumaCost/EstimateChromaCost's own cost is likewise pure bits (Av1RdCost.CombineCost
        // called with lambda == 1.0 there, see ComputeCandidateCost's lossless branch), so adding them
        // directly is already apples-to-apples. Non-lossless is different: its per-candidate cost is
        // SSE + lambda*bits (the same Lagrangian ComputeCandidateCost's non-lossless branch uses), typically
        // dominated by SSE (a real image block's squared error commonly runs into the thousands, versus a
        // handful of signaling bits) -- adding raw, un-scaled bit counts into that sum makes them near-total
        // noise in the leaf-vs-split comparison, understating their real influence by roughly 1/lambda. This
        // was a real, measured bug (not just an approximation): non-lossless partition RDO first landed
        // without this scaling and made this project's benchmark image *larger*, because the comparison was
        // effectively blind to the very signaling savings it exists to weigh. Scaling by s.Lambda converts
        // these bit counts into the same SSE-equivalent units ComputeCandidateCost already uses for
        // everything else in the comparison.
        long ScaleSignalingBits(long bits) => s.Lossless ? bits : (long)Math.Round(bits * s.Lambda);

        // This encoder's own non-lossless leaf-size floor (see the project plan's partition/TX-size RDO
        // phase: EncodeLeaf's non-lossless write path only knows how to commit 8x8/16x16/32x32 leaves, never
        // smaller) -- unlike the sizeMi == 1 floor above, a real PARTITION_NONE symbol is still written at
        // 8x8 (spec reads a partition symbol at every size down to and including 8x8, only sizes *below* 8x8
        // skip it), so its bits are still charged; there is just no PARTITION_SPLIT alternative to compare
        // against here, since a non-lossless leaf can never go smaller than this.
        if (!s.Lossless && sizeMi == 2)
        {
            long floorNoneBits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.None);
            return (Av1PartitionType.None, EstimateLumaCost(s, r, c, sizeMi) + EstimateChromaCost(s, r, c, sizeMi) + ScaleSignalingBits(LeafOtherSignalingCost + floorNoneBits));
        }

        long splitBits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.Split);
        int half = sizeMi / 2;
        long costSplit = ScaleSignalingBits(splitBits)
            + DecidePartition(s, r, c, half).Cost
            + DecidePartition(s, r, c + half, half).Cost
            + DecidePartition(s, r + half, c, half).Cost
            + DecidePartition(s, r + half, c + half, half).Cost;

        // This encoder's own non-lossless leaf-size ceiling: EncodeLeaf's non-lossless write path (and
        // ComputeCandidateCost's own scratch buffers, sized for this encoder's actual largest non-lossless
        // transform, 32x32) never go above sizeMi == 8 -- see the project plan's partition/TX-size RDO
        // phase. A 64x64 node (sizeMi == 16) therefore always splits for non-lossless, without ever calling
        // EstimateLumaCost at this level at all -- that call would overflow those buffers, not just lose
        // the leaf-vs-split comparison, so it must never run here, not merely never win.
        if (!s.Lossless && sizeMi == 16)
        {
            return (Av1PartitionType.Split, costSplit);
        }

        // Non-lossless leaves reaching this point are always sizeMi 4 or 8 (16x16/32x32 -- sizeMi == 16 was
        // already handled above, sizeMi == 2 below), i.e. exactly the sizes EncodeLeaf force-DC_PREDs chroma
        // for -- see EstimateChromaCost's own remarks for why this term is necessary here. Lossless never
        // needs it (its chroma cost doesn't depend on leaf size), so EstimateChromaCost's own MonoChrome
        // short-circuit aside, this is skipped entirely there rather than adding a zero no-op term.
        long noneBits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.None);
        long chromaCost = s.Lossless ? 0 : EstimateChromaCost(s, r, c, sizeMi);
        long lumaCost = EstimateLumaCost(s, r, c, sizeMi);

        // Palette (spec's own Block8x8-through-Block64x64 gate -- sizeMi == 1 never reaches this branch, see
        // the sizeMi == 1 case above; sizeMi > 16 is structurally never palette-eligible either, see
        // EncodeLeaf's paletteStructurallyPresent remarks -- real AV1 simply has no palette block-size CDF
        // context beyond 64x64) is a real alternative to regular-intra/WHT-residual coding for lossless
        // leaves, but was invisible to this comparison until now -- see EstimateLosslessPaletteCost's
        // remarks for why that under-used palette specifically on graphic/screen-content-style images.
        if (s.Lossless && sizeMi <= 16)
        {
            long? paletteCost = EstimateLosslessPaletteCost(s, r, c, sizeMi, availU: r > 0, availL: c > 0);
            if (paletteCost is long pc && pc < lumaCost)
            {
                lumaCost = pc;
            }
        }

        long costLeaf = lumaCost + chromaCost + ScaleSignalingBits(LeafOtherSignalingCost + noneBits);

        // Rectangular HORZ/VERT candidates (first increment of full AV1 partition-type support -- see
        // EncodeRectangularLeaf's own remarks for the scoping rationale): lossless only, and only for a
        // parent at or below 64x64 (sizeMi <= 16), so the resulting leaf is never bigger than 64x32/32x64 in
        // either dimension -- see EstimateRectLumaCost's remarks for why that keeps this out of the >64px
        // chunked residual path entirely. Unlike SPLIT, HORZ/VERT never recurse further: spec's own
        // partition_subsize() gives each half's *final* block size directly, so this only ever costs exactly
        // two leaves per candidate, not a recursive DecidePartition call.
        int bestType = Av1PartitionType.None;
        long bestCost = costLeaf;

        if (costSplit < bestCost)
        {
            bestType = Av1PartitionType.Split;
            bestCost = costSplit;
        }

        if (s.Lossless && sizeMi <= 16)
        {
            long horzBits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.Horz);
            long costHorz = ScaleSignalingBits(horzBits + (2 * LeafOtherSignalingCost))
                + EstimateRectLumaCost(s, r, c, sizeMi, half)
                + EstimateRectLumaCost(s, r + half, c, sizeMi, half);
            if (costHorz < bestCost)
            {
                bestType = Av1PartitionType.Horz;
                bestCost = costHorz;
            }

            long vertBits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.Vert);
            long costVert = ScaleSignalingBits(vertBits + (2 * LeafOtherSignalingCost))
                + EstimateRectLumaCost(s, r, c, half, sizeMi)
                + EstimateRectLumaCost(s, r, c + half, half, sizeMi);
            if (costVert < bestCost)
            {
                bestType = Av1PartitionType.Vert;
                bestCost = costVert;
            }
        }

        return (bestType, bestCost);
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
        // for pixel data via SourceY). Tried swapping in the real BlockDecoded-derived values (EncodeLeaf's
        // identical formula): passes every test (including RepeatedVerticalStripePattern_Lossless_
        // IntrabcRoundTripsExactlyAndStaysSmall, unlike an earlier, broader attempt bundling in real
        // filterTypeSmooth/YModes state too), but measured a net *loss* once both lossy and lossless are
        // weighed together on this project's benchmark image -- lossless improved by only 770 B (~0.04%)
        // while Quality=75/90 each grew (+568 B / +590 B) since this estimate is shared by both paths. Not
        // worth the added real/estimate divergence risk for a net-negative trade -- fixed, conservative
        // values keep the cost estimate a function of content and true structural position (availU/availL)
        // only, not of what else happened to be encoded first.
        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);
        var pred = s.Pred;
        int log2Size = PixelLog2(sizeMi);
        long bestCost = long.MaxValue;

        // Below Block8x8 (sizeMi == 1, the new 4x4 leaf floor), spec's intra_angle_info_y/_uv (§5.11.42/.43)
        // never reads an angle_delta symbol and always reconstructs with angleDelta == 0 regardless of mode
        // -- mirroring that here, not just at the write site (see EncodeLeaf's own AngleDelta write-site
        // remarks), is required for correctness, not just signaling economy: searching a nonzero angle_delta
        // this proxy or the real search could still "win" with, only to have the real decoder reconstruct
        // angleDelta == 0 instead, would desync this encoder's own recorded reconstruction from what a real
        // decoder produces for the same bitstream.
        bool angleDeltaAllowed = sizeMi >= 2;

        foreach (int mode in CandidateModes)
        {
            bool directional = Av1IntraMode.IsDirectional(mode) && angleDeltaAllowed;
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                long cost;
                if (sizePixels > 64)
                {
                    // Real AV1 intra prediction never spans more than 64x64 in one shot regardless of
                    // coding-block size (see ComputeLosslessWholeLeafCostPerSubBlock's remarks) -- only
                    // reachable for a lossless 128x128 superblock kept as one leaf.
                    cost = ComputeLosslessWholeLeafCostPerSubBlock(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, sizePixels, ptype: 0, s.YCoeffCtx, mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth: false, useRealBoundaryAvailability: false);
                }
                else
                {
                    Av1IntraPrediction.BuildEdges(above, left, s.SourceY, s.YWidth, x, y, sizePixels, sizePixels, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);
                    Av1IntraPrediction.Predict(pred, sizePixels, sizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.YWidth - 1, s.YHeight - 1, x, y, bitDepth: 8);
                    cost = ComputeCandidateCost(s, s.SourceY, s.YWidth, pred, x, y, sizePixels, ptype: 0, s.YCoeffCtx);
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                }
            }
        }

        return bestCost;
    }

    /// <summary>
    /// Rectangular (HORZ/VERT) counterpart to <see cref="EstimateLumaCost"/>, real per-4x4-sub-block WHT
    /// trial cost of a <paramref name="wMi"/>x<paramref name="hMi"/> (mi units) leaf at (<paramref name="r"/>,
    /// <paramref name="c"/>). Lossless-only (see <see cref="ComputeDecidePartition"/>'s Horz/Vert call site --
    /// non-lossless never calls this) and DC_PRED-only, deliberately: this is a first increment of rectangular
    /// partition support (see <see cref="EncodeRectangularLeaf"/>'s own remarks for the full scoping
    /// rationale) -- a real directional/angle_delta search, palette, and IntraBC for a rectangular leaf are
    /// all left for a follow-up, so there is exactly one candidate to cost here, not a search. Mirrors
    /// <see cref="EncodeRectangularLeaf"/>'s own per-sub-block, predict-from-progressively-updated-context
    /// shape, reading <see cref="TileState.SourceY"/> instead of <see cref="TileState.ReconY"/> for the same
    /// reason <see cref="EstimateLumaCost"/> does (see <see cref="DecidePartition"/>'s remarks: for lossless,
    /// a real commit's reconstruction is always bit-identical to source, so this is exact, not approximate,
    /// for the pixel data itself -- only haveAboveRight/haveBelowLeft fall back to their conservative
    /// "unavailable" default here, exactly like <see cref="EstimateLumaCost"/>'s own choice). Never reached
    /// for a leaf bigger than 64x64 in either dimension -- <see cref="ComputeDecidePartition"/> only ever
    /// offers Horz/Vert for a parent at or below sizeMi 16 (64x64), so the biggest rectangular candidate this
    /// ever costs is 64x32/32x64, never needing the &gt;64px chunked residual shape
    /// <see cref="EncodeLeaf"/>'s square path still handles separately.
    /// </summary>
    private static long EstimateRectLumaCost(TileState s, int r, int c, int wMi, int hMi)
    {
        int widthPixels = wMi * 4;
        int heightPixels = hMi * 4;
        int x = c * 4;
        int y = r * 4;

        var above = new Av1EdgeArray(16);
        var left = new Av1EdgeArray(16);
        var pred = s.Pred;
        var scratch = s.ScratchCoeffCtx;
        var trial = s.TrialSink;
        scratch.SeedFrom(s.YCoeffCtx, x >> 2, widthPixels >> 2, y >> 2, heightPixels >> 2);
        trial.Reset();

        int nW = widthPixels / 4;
        int nH = heightPixels / 4;
        var residual = s.Residual;
        var coeff = s.Coeff;
        var levels = s.Levels;

        for (int dr = 0; dr < nH; dr++)
        {
            for (int dc = 0; dc < nW; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                bool availU = subY > 0;
                bool availL = subX > 0;

                Av1IntraPrediction.BuildEdges(above, left, s.SourceY, s.YWidth, subX, subY, 4, 4, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);
                Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, above, left, Av1IntraMode.DcPred, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta: 0, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.YWidth - 1, s.YHeight - 1, subX, subY, bitDepth: 8);

                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((subY + i) * s.YWidth) + subX;
                    int predRowBase = i * 4;
                    for (int j = 0; j < 4; j++)
                    {
                        residual[(i * 4) + j] = s.SourceY[rowBase + j] - pred[predRowBase + j];
                    }
                }

                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                int subX4 = subX >> 2;
                int subY4 = subY >> 2;
                Av1CoefficientWriter.WriteCoeffs(trial, s.Cdf, levels, 4, ptype: 0, subX4, subY4, scratch, writeLumaTxType: null, blockSize: widthPixels, blockHeight: heightPixels, updateContext: true);
            }
        }

        return Av1RdCost.CombineCost(0, trial.Bits, 1.0);
    }

    /// <summary>
    /// Non-lossless-only chroma-cost counterpart to <see cref="EstimateLumaCost"/>, folded into
    /// <see cref="ComputeDecidePartition"/>'s leaf-cost estimate. Without this, the partition search only
    /// ever weighed luma's own signaling/residual savings from merging into a bigger leaf, completely blind
    /// to the real cost <see cref="EncodeLeaf"/>'s own forced-DC_PRED-chroma gate (<c>sizeMi &gt; 2</c>, see
    /// its remarks) imposes at 4:2:0 -- a real, measured regression (this project's benchmark image got
    /// larger, not smaller, once non-lossless partition/TX-size RDO first landed without this term). Lossless
    /// never needs this: its chroma always gets a real, unrestricted <see cref="SearchUvMode"/> search
    /// regardless of leaf size, so merging never costs it anything chroma-side.
    ///
    /// <para>Deliberately still just a single DC_PRED candidate whenever chromaN &gt; 1, even though
    /// <see cref="EncodeLeaf"/>'s own real chroma search (<see cref="SearchUvMode"/>) is no longer forced to
    /// DC_PRED there (now that <see cref="Av1ForwardTransform"/>'s forward ADST operators cover every chroma
    /// region size this encoder produces). Measured empirically: making this estimate run the same
    /// unrestricted mode/angle_delta sweep <see cref="SearchUvMode"/> does made the partition search
    /// systematically too optimistic about what merging into a bigger leaf would cost chroma-side --
    /// <see cref="SearchUvMode"/>'s real search picks whichever candidate wins by SSE-plus-rate against this
    /// leaf's own source pixels, but a mode that looks cheap in isolation here can still lose to what the
    /// actual encode achieves once real (not <see langword="false"/>-approximated) haveAboveRight/haveBelowLeft
    /// and real reconstructed neighbor context are available -- the mismatch measurably biased the search
    /// toward worse partitions (this project's benchmark image lost ~3 dB of PSNR at Quality=90 for no size
    /// win before this was reverted back to DC_PRED-only). A parent's leaf-vs-split comparison only needs this
    /// term to not systematically overstate what merging costs; DC_PRED is already a safe upper bound on that,
    /// since <see cref="SearchUvMode"/>'s real search can only ever do at least as well.</para>
    ///
    /// <para>Reads <see cref="TileState.SourceU"/>/<see cref="TileState.SourceV"/> for edge context, not
    /// <see cref="TileState.ReconU"/>/<see cref="TileState.ReconV"/> the way <see cref="SearchUvMode"/>'s real
    /// (post-commitment) search safely can -- mirroring <see cref="EstimateLumaCost"/>'s identical, already-
    /// proven choice and for the identical reason: during <see cref="DecidePartition"/>'s speculative
    /// recursion, no sibling's real reconstruction exists yet to read.</para>
    /// </summary>
    private static long EstimateChromaCost(TileState s, int r, int c, int sizeMi)
    {
        if (s.MonoChrome)
        {
            return 0;
        }

        int chromaN = sizeMi / 2;
        int chromaSizePixels = chromaN * 4;
        int cx = (c * 4) / 2;
        int cy = (r * 4) / 2;
        int log2Size = Log2FromPixels(chromaSizePixels);
        bool availU = r > 0;
        bool availL = c > 0;

        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);
        var pred = s.Pred;

        long CandidateCost(int mode, int angleDelta)
        {
            Av1IntraPrediction.BuildEdges(above, left, s.SourceU!, s.ChromaWidth, cx, cy, chromaSizePixels, chromaSizePixels, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);
            Av1IntraPrediction.Predict(pred, chromaSizePixels, chromaSizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.ChromaWidth - 1, s.ChromaHeight - 1, cx, cy, bitDepth: 8);
            long cost = ComputeCandidateCost(s, s.SourceU!, s.ChromaWidth, pred, cx, cy, chromaSizePixels, ptype: 1, s.UCoeffCtx!);

            Av1IntraPrediction.BuildEdges(above, left, s.SourceV!, s.ChromaWidth, cx, cy, chromaSizePixels, chromaSizePixels, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);
            Av1IntraPrediction.Predict(pred, chromaSizePixels, chromaSizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.ChromaWidth - 1, s.ChromaHeight - 1, cx, cy, bitDepth: 8);
            cost += ComputeCandidateCost(s, s.SourceV!, s.ChromaWidth, pred, cx, cy, chromaSizePixels, ptype: 1, s.VCoeffCtx!);
            return cost;
        }

        if (chromaN > 1)
        {
            return CandidateCost(Av1IntraMode.DcPred, 0);
        }

        long bestCost = long.MaxValue;
        foreach (int mode in CandidateModes)
        {
            bool directional = Av1IntraMode.IsDirectional(mode);
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                long cost = CandidateCost(mode, angleDelta);
                if (cost < bestCost)
                {
                    bestCost = cost;
                }
            }
        }

        return bestCost;
    }

    /// <summary>
    /// Trial-only bit cost (<see cref="Av1RdCost.CombineCost"/>, <c>sse: 0, lambda: 1.0</c> -- same
    /// zero-distortion convention as <see cref="ComputeCandidateCost"/>'s lossless branch, since a palette
    /// leaf reconstructs bit-exactly by construction, see <c>EncodeLeaf</c>'s <c>usedPalette</c> remarks) of
    /// covering this whole leaf with palette (Y, plus UV when <c>!s.MonoChrome</c> -- this encoder only ever
    /// uses palette all-or-nothing across both, see <c>EncodeLeaf</c>'s <c>usedPalette</c> remarks), or
    /// <see langword="null"/> when this leaf isn't palette-eligible on either plane (more than 8 distinct
    /// colors, or fewer than 2 -- <see cref="TryBuildYPalette"/>/<see cref="TryBuildUvPalette"/>'s own
    /// <c>PALETTE_MIN_SIZE</c> gate).
    ///
    /// <para>Feeds <see cref="ComputeDecidePartition"/>'s leaf-vs-split comparison. Without this, that
    /// comparison only ever weighed regular-intra/WHT-residual cost when deciding whether to merge into a
    /// bigger leaf -- completely blind to the fact that a bigger leaf can lose palette eligibility a smaller
    /// one wouldn't have, even when the regular-residual costs of the two choices look roughly break-even.
    /// This under-uses palette specifically on graphic/screen-content-style lossless images: a design element
    /// with only a handful of colors at a small leaf size can pick up enough anti-aliased/gradient pixels once
    /// merged into a bigger leaf to blow past the 8-color cap, and the old comparison had no way to see that
    /// coming. <c>EncodeLeaf</c>'s own real commit-time decision (<c>usedPalette</c>) doesn't need to change
    /// for this to take effect: it already uses palette unconditionally whenever eligible, so once this makes
    /// <see cref="DecidePartition"/> stop merging past that eligibility boundary, the leaves it now keeps
    /// smaller naturally pick up palette on their own.</para>
    ///
    /// <para>Mirrors <see cref="WritePaletteColorsY"/>/<see cref="WritePaletteColorsUv"/>/<see cref="WriteColorMapTokens"/>'s
    /// exact bit-cost shape (color-cache literals, delta-coded/flat color values, NS-coded first index,
    /// per-pixel color-index symbols) but never writes to <see cref="TileState.Symbols"/> or mutates any CDF --
    /// <c>WriteColorMapTokens</c>'s own <c>PaletteYColorIndex</c>/<c>PaletteUvColorIndex</c> CDFs are real,
    /// adaptively-updated state, so a speculative candidate that might not be chosen must never leave a trace
    /// there (same hazard <see cref="Av1CoefficientWriter.PlaneContext.SeedFrom"/>'s remarks describe for
    /// coefficient contexts) -- this reads them read-only via <see cref="Av1SymbolEncoder.EstimateSymbolCost"/>,
    /// the same safe pattern this method's own partition-bit costing already uses just above.</para>
    /// </summary>
    private static long? EstimateLosslessPaletteCost(TileState s, int r, int c, int sizeMi, bool availU, bool availL)
    {
        int sizePixels = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;

        if (!TryBuildYPalette(s, x, y, sizePixels, s.PaletteColorsY, out int nY))
        {
            return null;
        }

        long bits = EstimatePaletteColorBitsY(s, s.PaletteColorsY, nY, r, c, availU, availL);
        var colorMap = s.PaletteColorMap;
        BuildColorMap(s.SourceY, s.YWidth, x, y, sizePixels, s.PaletteColorsY, nY, colorMap);
        bits += EstimateColorMapBits(colorMap, sizePixels, nY, s.Cdf.PaletteYColorIndex);

        if (!s.MonoChrome)
        {
            if (!TryBuildUvPalette(s, x, y, sizePixels, s.PaletteColorsU, s.PaletteColorsV, out int nUv))
            {
                return null;
            }

            bits += EstimatePaletteColorBitsUv(s, s.PaletteColorsU, s.PaletteColorsV, nUv, r, c, availU, availL);
            BuildColorMapUv(s, x, y, sizePixels, s.PaletteColorsU, s.PaletteColorsV, nUv, colorMap);
            bits += EstimateColorMapBits(colorMap, sizePixels, nUv, s.Cdf.PaletteUvColorIndex);
        }

        return Av1RdCost.CombineCost(0, bits, 1.0);
    }

    /// <summary>Pure bit-count mirror of <see cref="WritePaletteColorsY"/> -- same color-cache/delta-width shrinking math, without ever writing to <see cref="TileState.Symbols"/> (see <see cref="EstimateLosslessPaletteCost"/>'s remarks on why a speculative candidate must not).</summary>
    private static long EstimatePaletteColorBitsY(TileState s, int[] colors, int n, int r, int c, bool availU, bool availL)
    {
        int nCache = GetPaletteCacheCount(s, 0, r, c, availU, availL);
        long bits = nCache + 8;
        if (n > 1)
        {
            bits += 2;
            const int minBits = 5;
            const int extraBits = 3;
            int widthBits = minBits + extraBits;
            int range = 256 - colors[0] - 1;
            for (int idx = 1; idx < n; idx++)
            {
                bits += widthBits;
                range -= colors[idx] - colors[idx - 1];
                widthBits = Math.Min(widthBits, Av1TileDecoder.CeilLog2(range));
            }
        }

        return bits;
    }

    /// <summary>Pure bit-count mirror of <see cref="WritePaletteColorsUv"/> -- see <see cref="EstimatePaletteColorBitsY"/>'s identical remarks.</summary>
    private static long EstimatePaletteColorBitsUv(TileState s, int[] uColors, int[] vColors, int n, int r, int c, bool availU, bool availL)
    {
        int nCache = GetPaletteCacheCount(s, 1, r, c, availU, availL);
        long bits = nCache + 8;
        if (n > 1)
        {
            bits += 2;
            const int minBits = 5;
            const int extraBits = 3;
            int widthBits = minBits + extraBits;
            int range = 256 - uColors[0];
            for (int idx = 1; idx < n; idx++)
            {
                bits += widthBits;
                range -= uColors[idx] - uColors[idx - 1];
                widthBits = Math.Min(widthBits, Av1TileDecoder.CeilLog2(range));
            }
        }

        bits += 1 + (n * 8);
        return bits;
    }

    /// <summary>
    /// Pure bit-count mirror of <see cref="WriteColorMapTokens"/> -- identical NS-coded first index plus
    /// wavefront-ordered, context-selected per-pixel symbol costs, but via <see cref="Av1SymbolEncoder.EstimateSymbolCost"/>
    /// against a <em>local, per-candidate scratch clone</em> of <paramref name="mapCdf"/>'s (small, fixed-size --
    /// spec's own <c>PALETTE_COLOR_INDEX_CONTEXTS</c> == 5) context rows, adapted in place via
    /// <see cref="Av1CdfAdaptation.AdaptCdf"/> after every pixel exactly like a real <see cref="TileState.Symbols"/>
    /// write would -- never touching <paramref name="mapCdf"/> itself.
    ///
    /// <para><b>This local adaptation is not optional.</b> A single palette leaf can carry thousands of pixels
    /// through the *same* handful of contexts (unlike coefficient coding's typically-sparse per-block symbol
    /// counts, where reading the frame's real, currently-adapted CDF once and reusing it for the whole trial is
    /// an acceptable approximation -- see <see cref="ComputeCandidateCost"/>'s remarks). Reading the same static,
    /// real CDF for every one of those pixels (this method's own first version) systematically overestimates a
    /// large uniform region's true cost by an order of magnitude or more: a real encoder's per-symbol cost
    /// collapses toward zero within the first few pixels as the CDF adapts to the region's dominant color, while
    /// a static read keeps charging close to <c>log2(n)</c> bits per pixel forever. That bug made every eligible
    /// palette candidate this method ever scored look far more expensive than regular intra/WHT residual coding
    /// -- confirmed by an instrumented run of <see cref="EstimateLosslessPaletteCost"/> on this project's
    /// benchmark image finding 2,946 structurally-eligible candidates across the partition search and zero
    /// wins, before this local-adaptation fix.</para>
    /// </summary>
    private static long EstimateColorMapBits(int[] colorMap, int size, int n, ushort[][][] mapCdf)
    {
        var colorOrder = new int[8];
        var inverseColorOrder = new int[8];

        const int contexts = 5; // spec PALETTE_COLOR_INDEX_CONTEXTS
        var scratchCdf = new ushort[contexts][];
        var seeded = new bool[contexts];

        long bits = Av1SymbolEncoder.EstimateNsCost(colorMap[0], n);

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

                if (!seeded[ctx])
                {
                    scratchCdf[ctx] = (ushort[])mapCdf[n - 2][ctx].Clone();
                    seeded[ctx] = true;
                }

                var cdf = scratchCdf[ctx];
                bits += Av1SymbolEncoder.EstimateSymbolCost(cdf, symbol);
                Av1CdfAdaptation.AdaptCdf(cdf, n, symbol);
            }
        }

        return bits;
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.ClearBlockDecodedFlags</c> (spec's <c>clear_block_decoded_flags(r, c, sbSize4)</c>, §5.11.3) -- <paramref name="sbSize4"/> matches whichever superblock size this frame actually uses (see <c>EncodeTile</c>'s remarks), and this encoder is always single-tile (MiColEnd/MiRowEnd == MiCols/MiRows).</summary>
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
        int subBlockMiRow = r & s.SbMiMask;
        int subBlockMiCol = c & s.SbMiMask;
        for (int i = 0; i < bh4; i++)
        {
            for (int j = 0; j < bw4; j++)
            {
                SetBlockDecoded(s, 0, subBlockMiRow + i, subBlockMiCol + j, true);
            }
        }
    }

    /// <summary>
    /// Chroma counterpart of <see cref="MarkLumaBlockDecoded"/> -- marks the <paramref name="sizeMi"/>-leaf's
    /// whole chroma sub-block grid decoded on both chroma planes, for the prediction paths (palette, IntraBC)
    /// that reconstruct all of a leaf's chroma in one shot rather than transform-block by transform-block
    /// (<see cref="EncodeChromaRegion"/> marks its own per-sub-block progress directly as it goes). Needed
    /// now that chroma directional prediction (<see cref="SearchUvMode"/>) reads real haveAboveRight/
    /// haveBelowLeft state for later leaves -- previously chroma was always DC_PRED, which never read this
    /// state, so leaving it unmarked here was harmless.
    /// </summary>
    private static void MarkChromaBlockDecoded(TileState s, int r, int c, int sizeMi)
    {
        int chromaN = s.Chroma444 ? sizeMi : sizeMi / 2;
        int subX = s.Chroma444 ? 0 : 1;
        int mult = s.Chroma444 ? 1 : 2;
        for (int dr = 0; dr < chromaN; dr++)
        {
            for (int dc = 0; dc < chromaN; dc++)
            {
                int row = ((r + (dr * mult)) & s.SbMiMask) >> subX;
                int col = ((c + (dc * mult)) & s.SbMiMask) >> subX;
                SetBlockDecoded(s, 1, row, col, true);
                SetBlockDecoded(s, 2, row, col, true);
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
    /// Chroma counterpart of <see cref="GetFilterType"/> (spec's <c>get_filter_type(plane)</c> for plane &gt; 0).
    /// The decoder's general version nudges the above/left neighbor mi position by one unit under 4:2:0
    /// subsampling (<c>_availUChroma</c>/<c>_availLChroma</c>'s "shares chroma with the sibling" case) --
    /// that nudge only matters for a 1-mi-tall/wide luma block, and its direction otherwise depends on the
    /// current block's mi row/col parity. This encoder's leaves are always 8x8 or bigger and always
    /// superblock-aligned to their own size (a power of two &gt;= 2 mi units), so every leaf's (r, c) is
    /// always even -- collapsing the decoder's parity-conditional nudge to the fixed offsets below (above:
    /// column +1 when subsampled; left: row +1 when subsampled), and making <c>availUChroma</c>/
    /// <c>availLChroma</c> always equal <paramref name="availU"/>/<paramref name="availL"/> (the sub-8x8
    /// deferral case never triggers here either). Computed once per coding block, matching
    /// <see cref="GetFilterType"/>'s own coding-block-scoped semantics.
    /// </summary>
    private static bool GetChromaFilterType(TileState s, int r, int c, bool availU, bool availL, bool subsampled)
    {
        bool aboveSmooth = availU && IsSmoothUvMode(s, r - 1, subsampled ? c + 1 : c);
        bool leftSmooth = availL && IsSmoothUvMode(s, subsampled ? r + 1 : r, c - 1);
        return aboveSmooth || leftSmooth;
    }

    private static bool IsSmoothUvMode(TileState s, int row, int col)
    {
        row = Math.Clamp(row, 0, s.MiRows - 1);
        col = Math.Clamp(col, 0, s.MiCols - 1);
        int mode = s.UvModes[(row * s.MiCols) + col];
        return mode is Av1IntraMode.SmoothPred or Av1IntraMode.SmoothVPred or Av1IntraMode.SmoothHPred;
    }

    /// <summary><c>log2</c> of an arbitrary power-of-two pixel size (4/8/16/32/64) -- unlike <see cref="PixelLog2"/>
    /// (which maps a *luma leaf's* mi-count, and so has no representation for a 4-pixel chroma region), this
    /// covers every chroma coding-block size a 4:2:0 leaf can produce, down to 4.</summary>
    private static int Log2FromPixels(int sizePixels) => sizePixels switch
    {
        64 => 6,
        32 => 5,
        16 => 4,
        8 => 3,
        _ => 2,
    };

    /// <summary>
    /// Real <c>D + lambda*R</c> Lagrangian RD cost (<see cref="Av1RdCost"/>) for one mode/angle/filter-intra
    /// candidate, replacing two previous proxies: non-lossless candidates used raw SSE with no rate term at
    /// all, and lossless candidates a hand-tuned WHT-coefficient-magnitude/log2 proxy that only approximated
    /// real bit cost (no CDF/context modeling). Both now share one real bit count instead: this actually
    /// forward-transforms, quantizes, and trial-costs (<see cref="Av1TrialSymbolSink"/>, via
    /// <see cref="Av1CoefficientWriter.WriteCoeffs"/> -- the exact same context-derivation code the real
    /// bitstream writer uses, so this is a real bit count, not an approximation of one) the residual this
    /// candidate's prediction would actually produce.
    ///
    /// <para>Non-lossless: forward DCT (<see cref="Av1ForwardTransform"/>) + quantize at
    /// <see cref="TileState.BaseQIdx"/>, cost = <c>sse + lambda*bits</c> (<see cref="Av1RdCost.CombineCost"/>).
    /// One caveat for chroma: this always transforms as DCT_DCT regardless of which <paramref name="ptype"/>
    /// 1 candidate <c>mode</c> is being scored, even though <c>EncodeChromaRegion</c>'s real (non-lossless)
    /// write later forward-transforms with a mode-dependent DCT/ADST-mixed type once a <c>uv_mode</c> is
    /// actually chosen (<c>Av1TxTypeTables.ModeToTxfm</c>, added for the real write path by PR #64) -- luma's
    /// tx_type is always DCT_DCT regardless of mode already (see <c>EncodeLeaf</c>'s <c>WriteLumaTxType</c>
    /// remarks), so this only approximates chroma. Real per-candidate transform-*type* search (this
    /// encoder's next RD-search phase, see the project plan) will let this also select the type each
    /// candidate's own mode would actually use; until then, a uniform DCT_DCT rate estimate still ranks
    /// candidates by real residual-energy-driven bit cost, just not necessarily the exact type each will
    /// finally use -- good enough for relative ranking, not for predicting the exact final byte count.</para>
    ///
    /// <para>Lossless: forward WHT (<see cref="Av1ForwardWht"/>, the real lossless transform -- see
    /// <see cref="EncodeLosslessLumaResidual"/>'s identical per-4x4-sub-block transform) + quantize at
    /// <c>baseQIdx == 0</c> (an exact identity, not a source of loss or extra bits), summing real bits across
    /// every 4x4 sub-block. Distortion is always exactly zero once a lossless candidate is really committed
    /// (every sub-block reconstructs bit-exactly), so the cost is bits alone (<see cref="Av1RdCost.CombineCost"/>
    /// called with <c>sse: 0, lambda: 1.0</c> -- not <see cref="TileState.Lambda"/>, which is 0 for lossless,
    /// meaning "don't weigh a meaningless zero-distortion term", the opposite of what's needed here).</para>
    ///
    /// <para>Both branches reseed <see cref="TileState.ScratchCoeffCtx"/> from the real, already-committed
    /// neighbor context (<paramref name="realCtx"/>) before trial-costing, mutate only the scratch copy
    /// (<c>updateContext: true</c> against scratch, matching real <c>WriteCoeffs</c> so a multi-sub-block
    /// lossless candidate's own later sub-blocks see its own earlier sub-blocks' context, exactly like a real
    /// encode would), and never write back to <paramref name="realCtx"/> itself -- a candidate that might not
    /// even be chosen must never leave a trace a later, real leaf's context lookup could read (see
    /// <see cref="Av1CoefficientWriter.PlaneContext.SeedFrom"/>'s remarks). This is the same "estimate from
    /// real, already-committed neighbors; not-yet-encoded siblings use safe defaults" posture
    /// <see cref="DecidePartition"/>'s own remarks already document for the pixel data (<see cref="TileState.SourceY"/>
    /// vs <see cref="TileState.ReconY"/>) -- context estimation inherits the identical approximation for the
    /// identical reason.</para>
    ///
    /// <para>Takes an explicit <paramref name="source"/>/<paramref name="planeWidth"/> (rather than always
    /// reading <see cref="TileState.SourceY"/>/<see cref="TileState.YWidth"/>) and <paramref name="ptype"/>/<paramref name="realCtx"/>,
    /// so the same cost function scores luma (<c>ptype: 0</c>, <see cref="TileState.YCoeffCtx"/>) and chroma
    /// (<c>ptype: 1</c>, <see cref="TileState.UCoeffCtx"/>/<see cref="TileState.VCoeffCtx"/>) candidates alike
    /// during the UV mode search (see <see cref="SearchUvMode"/>).</para>
    /// </summary>
    private static long ComputeCandidateCost(TileState s, int[] source, int planeWidth, int[] pred, int x, int y, int sizePixels, int ptype, Av1CoefficientWriter.PlaneContext realCtx)
    {
        int x4 = x >> 2;
        int y4 = y >> 2;
        var scratch = s.ScratchCoeffCtx;
        var trial = s.TrialSink;
        int[] coeff = s.Coeff;
        int[] levels = s.Levels;

        if (!s.Lossless)
        {
            long sse = 0;
            int[] residual = s.Residual;
            for (int i = 0; i < sizePixels; i++)
            {
                int rowBase = ((y + i) * planeWidth) + x;
                int predRowBase = i * sizePixels;
                for (int j = 0; j < sizePixels; j++)
                {
                    int diff = source[rowBase + j] - pred[predRowBase + j];
                    sse += (long)diff * diff;
                    residual[(i * sizePixels) + j] = diff;
                }
            }

            Av1ForwardTransform.Forward2D(residual, coeff, sizePixels);
            Av1ForwardQuantizer.Quantize(coeff, levels, sizePixels, s.BaseQIdx);

            int w4 = sizePixels >> 2;
            scratch.SeedFrom(realCtx, x4, w4, y4, w4);
            trial.Reset();
            Av1CoefficientWriter.WriteCoeffs(trial, s.Cdf, levels, sizePixels, ptype, x4, y4, scratch, writeLumaTxType: null, updateContext: true);

            return Av1RdCost.CombineCost(sse, trial.Bits, s.Lambda);
        }

        int leafW4 = sizePixels >> 2;
        scratch.SeedFrom(realCtx, x4, leafW4, y4, leafW4);
        trial.Reset();
        int[] subResidual = s.Residual;

        for (int by = 0; by < sizePixels; by += 4)
        {
            for (int bx = 0; bx < sizePixels; bx += 4)
            {
                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((y + by + i) * planeWidth) + x + bx;
                    int predRowBase = ((by + i) * sizePixels) + bx;
                    for (int j = 0; j < 4; j++)
                    {
                        subResidual[(i * 4) + j] = source[rowBase + j] - pred[predRowBase + j];
                    }
                }

                Av1ForwardWht.Forward4x4(subResidual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                int subX4 = (x + bx) >> 2;
                int subY4 = (y + by) >> 2;
                Av1CoefficientWriter.WriteCoeffs(trial, s.Cdf, levels, 4, ptype, subX4, subY4, scratch, writeLumaTxType: null, blockSize: sizePixels, updateContext: true);
            }
        }

        return Av1RdCost.CombineCost(0, trial.Bits, 1.0);
    }

    /// <summary>
    /// Real per-4x4-sub-block Lagrangian cost (<see cref="Av1RdCost.CombineCost"/>, <c>sse: 0, lambda: 1.0</c>)
    /// of a candidate (<paramref name="mode"/>, <paramref name="angleDelta"/>, filter-intra) across a lossless
    /// leaf bigger than 64x64 (128x128, the only size this ever runs at -- see <see cref="EstimateLumaCost"/>/
    /// <c>EncodeLeaf</c>'s own call sites), by genuinely re-predicting fresh at each 4x4 sub-block instead of
    /// <see cref="ComputeCandidateCost"/>'s whole-leaf single-shot shortcut. That shortcut (build edges once
    /// from the leaf's own outer boundary, predict the whole leaf in one call) is safe up to 64x64 because
    /// real AV1 intra prediction is architecturally capped at 64x64 per prediction/transform unit regardless
    /// of coding-block size -- <see cref="Av1IntraPrediction.PredictSmooth"/>'s own <c>Sm_Weights</c> tables
    /// simply don't exist beyond that (spec never predicts a bigger unit in one shot). Every coding block
    /// above 64x64 -- which for lossless (always <c>TX_4X4</c> regardless of coding-block size) means every
    /// lossless leaf above 4x4 already, this method is only reached at 128x128 specifically because 64x64
    /// still fits the old shortcut -- is really predicted as a grid of smaller units, each with its own fresh
    /// neighbor context; <c>EncodeLosslessLumaResidual</c> already implements exactly this at 4x4 granularity
    /// for the real residual commit, and this mirrors it for candidate costing.
    ///
    /// <para>Reads <paramref name="source"/> for both prediction input and residual (safe, not approximate,
    /// for lossless -- source is bit-identical to final reconstruction once a candidate is really committed,
    /// the same substitution <see cref="DecidePartition"/>'s own remarks already document). Never mutates
    /// <see cref="TileState.Symbols"/>, any real CDF, or <see cref="TileState.BlockDecoded"/> -- interior
    /// sub-block-to-sub-block availability (has this leaf's own earlier sub-block, in raster order, already
    /// been virtually processed) is derived arithmetically from the sub-block's own <c>(dr, dc)</c> grid
    /// position alone (provably identical to what a real <see cref="GetBlockDecoded"/> read would give for a
    /// raster-order-earlier interior neighbor -- see this method's own remarks at the call site for the
    /// derivation), so no scratch/restore bookkeeping is needed the way coefficient contexts require.</para>
    ///
    /// <para>Boundary (leaf-external) neighbor availability is controlled by
    /// <paramref name="useRealBoundaryAvailability"/>: <see langword="false"/> (used by
    /// <see cref="EstimateLumaCost"/>'s speculative partition-size search) always treats a boundary-crossing
    /// neighbor as unavailable, matching every other whole-leaf estimate in this class and avoiding the
    /// measured, position-dependent IntraBC-repeat regression real boundary reads caused there (see
    /// <see cref="EstimateLumaCost"/>'s own remarks); <see langword="true"/> (used by <c>EncodeLeaf</c>'s real,
    /// already-size-committed mode search) reads real <see cref="TileState.BlockDecoded"/> state instead,
    /// matching <c>EncodeLosslessLumaResidual</c>'s real behavior exactly so the mode this picks is scored the
    /// same way its real residual commit will be.</para>
    /// </summary>
    private static long ComputeLosslessWholeLeafCostPerSubBlock(TileState s, int[] source, int planeWidth, int planeHeight, int r, int c, int x, int y, int sizePixels, int ptype, Av1CoefficientWriter.PlaneContext realCtx, int mode, int angleDelta, bool useFilterIntra, int filterIntraMode, bool filterTypeSmooth, bool useRealBoundaryAvailability)
    {
        int n = sizePixels / 4;
        int x4 = x >> 2;
        int y4 = y >> 2;
        var scratch = s.ScratchCoeffCtx;
        scratch.SeedFrom(realCtx, x4, n, y4, n);
        var trial = s.TrialSink;
        trial.Reset();

        int blockDecodedPlane = ptype == 0 ? 0 : 1;
        int subBlockMiRowBase = r & s.SbMiMask;
        int subBlockMiColBase = c & s.SbMiMask;

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
                int subBlockMiRow = subBlockMiRowBase + dr;
                int subBlockMiCol = subBlockMiColBase + dc;

                bool haveAboveRight;
                bool haveBelowLeft;
                if (useRealBoundaryAvailability)
                {
                    haveAboveRight = GetBlockDecoded(s, blockDecodedPlane, subBlockMiRow - 1, subBlockMiCol + 1);
                    haveBelowLeft = GetBlockDecoded(s, blockDecodedPlane, subBlockMiRow + 1, subBlockMiCol - 1);
                }
                else
                {
                    // Interior above-right (dr > 0 && dc + 1 < n) was visited at raster index (dr-1)*n+(dc+1),
                    // always < the current dr*n+dc, so it's provably already available -- exactly what a real
                    // GetBlockDecoded read would show. A leaf's own bottom-left is never interior in a raster
                    // (top-to-bottom, left-to-right) scan (the row below is never visited yet), matching real
                    // AV1 decode order too. Boundary (leaf-external) positions stay conservatively false --
                    // see this method's own remarks on why.
                    haveAboveRight = dr > 0 && (dc + 1) < n;
                    haveBelowLeft = false;
                }

                var above = new Av1EdgeArray(16);
                var left = new Av1EdgeArray(16);
                Av1IntraPrediction.BuildEdges(above, left, source, planeWidth, subX, subY, 4, 4, availL, availU, haveAboveRight, haveBelowLeft, planeWidth - 1, planeHeight - 1, bitDepth: 8);

                var pred = s.Pred;
                Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, above, left, mode, availL, availU, useFilterIntra, filterIntraMode, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, planeWidth - 1, planeHeight - 1, subX, subY, bitDepth: 8);

                var residual = s.Residual;
                for (int i = 0; i < 16; i++)
                {
                    residual[i] = source[((subY + (i / 4)) * planeWidth) + subX + (i % 4)] - pred[i];
                }

                var coeff = s.Coeff;
                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                var levels = s.Levels;
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                int subX4 = subX >> 2;
                int subY4 = subY >> 2;
                Av1CoefficientWriter.WriteCoeffs(trial, s.Cdf, levels, 4, ptype, subX4, subY4, scratch, writeLumaTxType: null, blockSize: sizePixels, updateContext: true);
            }
        }

        return Av1RdCost.CombineCost(0, trial.Bits, 1.0);
    }

    /// <summary>
    /// Non-lossless tx_type-only candidate cost (Phase 4 of the project plan): same transform+quantize+
    /// entropy-trial pipeline <see cref="ComputeCandidateCost"/>'s non-lossless branch uses, but taking an
    /// already-computed <paramref name="residual"/>/<paramref name="sse"/> (constant across every candidate
    /// once the leaf's prediction is fixed, see the call site's remarks) and a <paramref name="txType"/> to
    /// try, instead of re-deriving the residual from a fresh prediction every time -- this is only ever
    /// called after mode/angle search has already picked a winning prediction, refining just the transform
    /// choice against it, so re-gathering the (unchanged) residual/SSE for each of the reduced set's 5
    /// candidates would be pure waste.
    /// </summary>
    private static long ComputeTxTypeCost(TileState s, int[] residual, long sse, int sizePixels, int ptype, int x, int y, Av1CoefficientWriter.PlaneContext realCtx, int txType)
    {
        int x4 = x >> 2;
        int y4 = y >> 2;
        int[] coeff = s.Coeff;
        int[] levels = s.Levels;
        var scratch = s.ScratchCoeffCtx;
        var trial = s.TrialSink;

        Av1ForwardTransform.Forward2D(residual, coeff, sizePixels, txType);
        Av1ForwardQuantizer.Quantize(coeff, levels, sizePixels, s.BaseQIdx);

        int w4 = sizePixels >> 2;
        scratch.SeedFrom(realCtx, x4, w4, y4, w4);
        trial.Reset();
        Av1CoefficientWriter.WriteCoeffs(trial, s.Cdf, levels, sizePixels, ptype, x4, y4, scratch, writeLumaTxType: null, updateContext: true);

        return Av1RdCost.CombineCost(sse, trial.Bits, s.Lambda);
    }

    /// <summary>
    /// Phase 5 of the project plan: post-quantization rate-distortion refinement ("trellis" quantization,
    /// libaom's <c>av1_optimize_txb</c>/<c>optimize_txb</c>, <c>av1/encoder/encodetxb.c</c>) for one
    /// already-quantized, non-lossless transform block. Rounding each coefficient to its nearest
    /// reconstructible value (what <see cref="Av1ForwardQuantizer.Quantize"/> already does) minimizes each
    /// coefficient's own distortion in isolation, but ignores that a level's magnitude also drives its own
    /// entropy cost (coeff_base/coeff_br symbol size) and every later-coded coefficient's context -- so a
    /// small further reduction can sometimes trade a little distortion for enough bits to be a net win.
    ///
    /// <para><b>Distortion is measured in the coefficient (transform) domain, not the pixel domain</b> --
    /// <c>(coeff[pos] - dqCandidate)^2</c>, where <paramref name="coeff"/> is this block's real, pre-
    /// quantization forward-transform output (<see cref="Av1ForwardTransform.Forward2D"/>'s own output, the
    /// same array <see cref="Av1ForwardQuantizer.Quantize"/> just quantized <paramref name="levels"/> from)
    /// and <c>dqCandidate</c> is the trial level's dequantized value, computed the same way
    /// <see cref="Av1Dequantizer.Dequantize"/> computes it for real reconstruction (<c>level * q / dqDenom</c>,
    /// truncating -- <paramref name="coeff"/> and a real dequantized level are already the same "coefficient
    /// units" in this codebase's own forward/backward quantizer pair, by construction, so no extra libaom-style
    /// rescale is needed here the way it would be to compensate for libaom's own internal fixed-point
    /// convention). This mirrors libaom's own <c>get_coeff_dist</c> exactly in kind (coefficient-domain, not
    /// pixel-domain distortion) -- an earlier version of this method used real pixel-domain SSE (dequantize +
    /// full inverse-transform + compare to source per trial), which was both far more expensive per trial and,
    /// worse, measurably wrong: at the <em>same</em> <see cref="TileState.Lambda"/> this encoder's mode/tx_type
    /// search already uses, pixel-domain SSE at this pass's much finer per-coefficient granularity was
    /// systematically outweighed by the rate term, and the result was provably worse (bigger <em>and</em>
    /// lower PSNR) than simply picking a different qindex at the same output size -- measured on this
    /// project's own benchmark image before this coefficient-domain rewrite. Switching to a coefficient-domain
    /// distortion metric restores the same "units" convention libaom's own trellis lambda derivation assumes.
    /// </para>
    ///
    /// <para><paramref name="ptype"/> also selects libaom's own real, measured per-plane trellis rd-multiplier
    /// (<c>plane_rd_mult</c> in <c>av1/encoder/encodetxb.c</c>, intra row: luma 17, chroma 13, applied as
    /// <c>(rdmult * planeMult) &gt;&gt; 2</c>) layered on top of <see cref="TileState.Lambda"/> -- not an
    /// independently-guessed scale factor (the project's own established caution against empirically-guessed
    /// lambda scaling, see <see cref="Av1RdCost.QIndexToLambda"/>'s remarks, is about guessing a fudge factor
    /// with no reference basis; this is libaom's own real, shipped calibration for exactly this per-coefficient
    /// decision, deliberately distinct from the coarser mode/partition/tx_type lambda -- reusing that lambda
    /// unscaled for trellis was the bug the coefficient-domain rewrite above fixes, and this per-plane factor
    /// is the other half of libaom's own trellis-specific calibration).</para>
    ///
    /// <para>Only ever reduces a level's magnitude by one step, never increases it or explores further steps
    /// (nearest-rounding already chose the distortion-minimizing point in isolation, so only trading some of
    /// that away for fewer bits can help, never the reverse), processed from the last (highest-frequency,
    /// closest to eob) nonzero coefficient to the first -- the same scan order
    /// <see cref="Av1CoefficientWriter.WriteCoeffs"/> itself serializes in, so dropping the block's current
    /// last nonzero coefficient to zero shrinks eob (and every bit <c>coeff_base_eob</c>/<c>eob_pt</c> would
    /// otherwise spend past it) exactly the way a real encode would. Rate is still measured exactly (a trial
    /// <see cref="Av1CoefficientWriter.WriteCoeffs"/> call against the real, already-committed neighbor
    /// context, the same mechanism <see cref="ComputeCandidateCost"/> already uses) -- only the distortion side
    /// changed; getting the rate side approximately right would reintroduce exactly the kind of
    /// hard-to-diagnose miscalibration this rewrite exists to fix.</para>
    ///
    /// <para>Only ever considers levels within <c>Av1CoeffTables.NumBaseLevels + 1</c> (the loop body's own
    /// remarks explain why) -- with that restriction in place plus the two fixes above, this project's own
    /// benchmark image measured a real, Pareto-improving trade at every tested Quality: smaller output at
    /// only a modest quality cost relative to simply picking a different qindex at the same size (e.g.
    /// Quality=90: ~8% smaller for ~0.45 dB, vs. ~1.9 dB for the same size reduction before this restriction
    /// existed) -- verified directly against real (not interpolated) same-size comparison points, not just
    /// this encoder's own <see cref="Av1RdCost"/> metric, since that metric is exactly what a miscalibrated
    /// lambda would silently agree with itself about.</para>
    /// </summary>
    private static void OptimizeCoeffTrellis(TileState s, int[] coeff, int[] levels, int sizePixels, int ptype, int x4, int y4, Av1CoefficientWriter.PlaneContext realCtx)
    {
        int txSz = Av1ForwardTransform.SizeToTxSz(sizePixels);
        int dcQ = Av1Dequantizer.DcQ(s.BaseQIdx, 8);
        int acQ = Av1Dequantizer.AcQ(s.BaseQIdx, 8);
        int dqDenom = txSz == Av1TxSize.Tx32x32 ? 2 : 1;
        int planeMult = ptype == 0 ? 17 : 13;
        double trellisLambda = s.Lambda * planeMult / 4.0;

        int[] scan = Av1ScanTables.GetScan(txSz, Av1TxType.DctDct);
        int total = sizePixels * sizePixels;
        int w4 = sizePixels >> 2;
        var scratch = s.ScratchCoeffCtx;
        var trial = s.TrialSink;

        long GetCoeffDist(int pos)
        {
            int level = levels[pos];
            long q = pos == 0 ? dcQ : acQ;
            long dq = (Math.Abs((long)level) * q) / dqDenom;
            long diff = coeff[pos] - (level < 0 ? -dq : dq);
            return diff * diff;
        }

        long GetCost(long distortion)
        {
            scratch.SeedFrom(realCtx, x4, w4, y4, w4);
            trial.Reset();
            Av1CoefficientWriter.WriteCoeffs(trial, s.Cdf, levels, sizePixels, ptype, x4, y4, scratch, writeLumaTxType: null, updateContext: true);
            return Av1RdCost.CombineCost(distortion, trial.Bits, trellisLambda);
        }

        long currentDist = 0;
        for (int c = 0; c < total; c++)
        {
            int pos = scan[c];
            if (levels[pos] != 0)
            {
                currentDist += GetCoeffDist(pos);
            }
        }

        long currentCost = GetCost(currentDist);

        for (int c = total - 1; c >= 0; c--)
        {
            int pos = scan[c];
            int level = levels[pos];

            // Restricted to levels the coeff_base/coeff_base_eob symbol alone already represents
            // (Av1CoeffTables.NumBaseLevels + 1 == 3, spec's own base-symbol ceiling before WriteCoeffs's
            // coeff_br loop kicks in -- see its cappedLevel > NumBaseLevels branch) -- measured, not assumed:
            // trying every nonzero level regardless of magnitude captured essentially the same size
            // reduction as this narrower search but at real, measurable extra distortion cost (~1.6 dB PSNR
            // at Quality=90 on this project's own benchmark image, for a difference in output size under
            // 0.1%). A one-step reduction on a level already past this boundary rarely changes which
            // coeff_br symbol gets written (the br loop's own granularity absorbs it), so it pays close to
            // the full quadratic distortion cost of the step for comparatively little of the rate benefit
            // that makes the trade worthwhile at smaller levels.
            if (level == 0 || Math.Abs(level) > Av1CoeffTables.NumBaseLevels + 1)
            {
                continue;
            }

            long originalDistContribution = GetCoeffDist(pos);
            int originalLevel = level;
            levels[pos] = level > 0 ? level - 1 : level + 1;

            long candidateDist = currentDist - originalDistContribution + GetCoeffDist(pos);
            long candidateCost = GetCost(candidateDist);
            if (candidateCost < currentCost)
            {
                currentCost = candidateCost;
                currentDist = candidateDist;
            }
            else
            {
                levels[pos] = originalLevel;
            }
        }
    }

    /// <summary>
    /// Real cost-based UV mode + angle_delta search, replacing the previous hardcoded DC_PRED -- mirrors
    /// <see cref="EncodeLeaf"/>'s own whole-leaf luma search (same <see cref="CandidateModes"/>/angle_delta
    /// sweep, same <see cref="ComputeCandidateCost"/> proxy), except a single <c>uv_mode</c> covers both U and
    /// V (spec §5.11.42: one mode/angle pair per coding block, not per chroma plane), so each candidate's cost
    /// is the sum of both planes' cost. Builds edges from <see cref="TileState.ReconU"/>/<see cref="TileState.ReconV"/>
    /// (already-real neighbor reconstruction, exactly like the luma search reads <see cref="TileState.ReconY"/>)
    /// at the coding block's full chroma-region size -- <see cref="EncodeChromaRegion"/>'s real write later
    /// re-predicts per-4x4 sub-block from progressively reconstructed neighbors, same relationship the luma
    /// leaf search already has with <see cref="EncodeLosslessLumaResidual"/>.
    ///
    /// <para>haveAboveRight/haveBelowLeft are read from the real per-plane <see cref="TileState.BlockDecoded"/>
    /// state (previously only ever populated/queried for plane 0 -- chroma always used DC_PRED, which never
    /// reads past the block's own edges) at the *coding-block* granularity, the same approximation the luma
    /// whole-leaf search already makes for its own single edge build (see that search's remarks) -- U and V
    /// share identical geometry, so plane 1 (U)'s state stands in for both here; the real per-4x4-sub-block
    /// values <see cref="EncodeChromaRegion"/> uses are exact, not approximated.</para>
    /// </summary>
    private static (int Mode, int AngleDelta, int AlphaU, int AlphaV) SearchUvMode(TileState s, int r, int c, int x, int y, int sizeMi, bool availU, bool availL)
    {
        bool subsampled = !s.Chroma444;
        int chromaN = s.Chroma444 ? sizeMi : sizeMi / 2;
        int cx = s.Chroma444 ? x : x / 2;
        int cy = s.Chroma444 ? y : y / 2;
        int chromaSizePixels = chromaN * 4;
        int log2Size = Log2FromPixels(chromaSizePixels);

        int subX = subsampled ? 1 : 0;
        int chromaRow = (r & s.SbMiMask) >> subX;
        int chromaCol = (c & s.SbMiMask) >> subX;
        bool haveAboveRight = GetBlockDecoded(s, 1, chromaRow - 1, chromaCol + chromaN);
        bool haveBelowLeft = GetBlockDecoded(s, 1, chromaRow + chromaN, chromaCol - 1);
        bool filterTypeSmooth = GetChromaFilterType(s, r, c, availU, availL, subsampled);

        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);
        var pred = s.Pred;
        int bestMode = Av1IntraMode.DcPred;
        int bestAngleDelta = 0;
        int bestAlphaU = 0;
        int bestAlphaV = 0;
        long bestCost = long.MaxValue;

        // Gated on the luma leaf's own sizeMi (matching intra_angle_info_uv()'s _miSize -- the coding
        // block's size, not the chroma plane's residual size) -- see EstimateLumaCost's identical
        // angleDeltaAllowed remarks for why this must match the real decoder's angleDelta == 0 default below
        // Block8x8, not just skip signaling for it.
        bool angleDeltaAllowed = sizeMi >= 2;

        foreach (int mode in CandidateModes)
        {
            bool directional = Av1IntraMode.IsDirectional(mode) && angleDeltaAllowed;
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                long cost;
                if (chromaSizePixels > 64)
                {
                    // See ComputeLosslessWholeLeafCostPerSubBlock's remarks (only reachable for a lossless
                    // 4:4:4 128x128 chroma region, paired 1:1 with a 128x128 luma leaf). Real boundary
                    // availability, matching this real (already-size-committed) mode decision's own
                    // haveAboveRight/haveBelowLeft above.
                    cost = ComputeLosslessWholeLeafCostPerSubBlock(s, s.SourceU!, s.ChromaWidth, s.ChromaHeight, r, c, cx, cy, chromaSizePixels, ptype: 1, s.UCoeffCtx!, mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability: true);
                    cost += ComputeLosslessWholeLeafCostPerSubBlock(s, s.SourceV!, s.ChromaWidth, s.ChromaHeight, r, c, cx, cy, chromaSizePixels, ptype: 1, s.VCoeffCtx!, mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability: true);
                }
                else
                {
                    Av1IntraPrediction.BuildEdges(above, left, s.ReconU!, s.ChromaWidth, cx, cy, chromaSizePixels, chromaSizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);
                    Av1IntraPrediction.Predict(pred, chromaSizePixels, chromaSizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.ChromaWidth - 1, s.ChromaHeight - 1, cx, cy, bitDepth: 8);
                    cost = ComputeCandidateCost(s, s.SourceU!, s.ChromaWidth, pred, cx, cy, chromaSizePixels, ptype: 1, s.UCoeffCtx!);

                    Av1IntraPrediction.BuildEdges(above, left, s.ReconV!, s.ChromaWidth, cx, cy, chromaSizePixels, chromaSizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);
                    Av1IntraPrediction.Predict(pred, chromaSizePixels, chromaSizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.ChromaWidth - 1, s.ChromaHeight - 1, cx, cy, bitDepth: 8);
                    cost += ComputeCandidateCost(s, s.SourceV!, s.ChromaWidth, pred, cx, cy, chromaSizePixels, ptype: 1, s.VCoeffCtx!);
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                    bestAngleDelta = angleDelta;
                }
            }
        }

        // CFL (Phase 6 backlog item of the project plan), non-lossless only -- see TryCflCandidate's remarks
        // for why lossless is out of scope here. Competes fairly against every mode above via the same real
        // ComputeCandidateCost trial cost, just with a linear-model prediction (Av1IntraPrediction's own
        // decode-side PredictChromaFromLuma math, spec §7.11.5) instead of a fixed directional/smooth pattern.
        if (!s.Lossless)
        {
            long cflCost = TryCflCandidate(s, x, y, cx, cy, chromaSizePixels, log2Size, subX, availL, availU, haveAboveRight, haveBelowLeft, filterTypeSmooth, above, left, pred, out int cflAlphaU, out int cflAlphaV);
            if (cflCost < bestCost)
            {
                bestCost = cflCost;
                bestMode = Av1IntraMode.UvCflPred;
                bestAngleDelta = 0;
                bestAlphaU = cflAlphaU;
                bestAlphaV = cflAlphaV;
            }
        }

        return (bestMode, bestAngleDelta, bestAlphaU, bestAlphaV);
    }

    /// <summary>
    /// CFL (chroma-from-luma, spec §7.11.5) candidate for <see cref="SearchUvMode"/> -- non-lossless only:
    /// lossless's WHT/pure-bits cost domain and its own already-correct <c>cflAllowed</c> CDF-selection logic
    /// in <see cref="EncodeLeaf"/> are left untouched (that logic already handles CFL-eligibility correctly
    /// for both lossless and non-lossless, independent of whether CFL is ever actually searched); this is
    /// scoped to the lossy path the project plan explicitly flagged CFL as a real, unexploited opportunity
    /// for.
    ///
    /// <para>U and V are searched independently -- CFL's alpha_u/alpha_v each only affect their own plane's
    /// prediction -- around a fast least-squares estimate of the alpha that best explains this block's real
    /// chroma AC content from its own luma AC content (<c>alpha* = sum(lumaAc*chromaAc) / sum(lumaAc^2)</c>,
    /// the closed-form minimizer of squared prediction error before quantization/entropy cost is considered
    /// at all); a small window around that estimate is then trial-costed for real via the same
    /// <see cref="ComputeCandidateCost"/> every other candidate in <see cref="SearchUvMode"/> uses, so the
    /// actual accept/reject decision is never based on the (rate-blind) estimate itself. alpha_u/alpha_v
    /// share one bitstream sign symbol (spec's <c>cfl_alpha_signs</c> -- (0,0) isn't an encodable combination,
    /// at least one channel must be nonzero), so the two planes' independently-best alphas are combined and
    /// the real joint signaling cost (<see cref="WriteCflAlphas"/>, trial-costed exactly like the residual
    /// bits are) is added once here, not per-plane.</para>
    /// </summary>
    private static long TryCflCandidate(TileState s, int lumaX, int lumaY, int cx, int cy, int chromaSizePixels, int log2Size, int subX, bool availL, bool availU, bool haveAboveRight, bool haveBelowLeft, bool filterTypeSmooth, Av1EdgeArray above, Av1EdgeArray left, int[] pred, out int alphaU, out int alphaV)
    {
        int[] lumaAcU = s.CflLumaAc;
        long lumaAvgU = ComputeCflLumaAc(s.ReconY, s.YWidth, lumaX, lumaY, chromaSizePixels, log2Size, subX, lumaAcU);

        long costU = TryCflPlane(s, s.ReconU!, s.SourceU!, s.UCoeffCtx!, cx, cy, chromaSizePixels, log2Size, availL, availU, haveAboveRight, haveBelowLeft, filterTypeSmooth, above, left, pred, lumaAcU, lumaAvgU, out alphaU);

        // U's own AC buffer is fully consumed (every read of it happens inside TryCflPlane's own
        // alpha-candidate loop, via ApplyCflAlpha) before V starts, so reusing the same TileState.CflLumaAc
        // scratch for both planes -- like SearchUvMode's own pred buffer -- is safe. This must stay a
        // dedicated buffer, not TileState.Residual: ComputeCandidateCost's own non-lossless branch (called
        // from inside TryCflPlane's loop, once per alpha candidate) clobbers Residual as its own scratch on
        // every call, which would silently feed ApplyCflAlpha stale pixel-residual data instead of real luma
        // AC values from the second alpha candidate onward.
        int[] lumaAcV = s.CflLumaAc;
        long lumaAvgV = ComputeCflLumaAc(s.ReconY, s.YWidth, lumaX, lumaY, chromaSizePixels, log2Size, subX, lumaAcV);
        long costV = TryCflPlane(s, s.ReconV!, s.SourceV!, s.VCoeffCtx!, cx, cy, chromaSizePixels, log2Size, availL, availU, haveAboveRight, haveBelowLeft, filterTypeSmooth, above, left, pred, lumaAcV, lumaAvgV, out alphaV);

        if (alphaU == 0 && alphaV == 0)
        {
            // Not spec-encodable (cfl_alpha_signs has no (zero, zero) symbol -- ReadCflAlphas's own
            // signU/signV derivation never produces this pair) and not useful anyway: zero alpha on both
            // planes means CFL's AC term vanishes entirely, so this would just be a strictly more expensive
            // way to signal what DC_PRED already offers for free.
            return long.MaxValue;
        }

        var trial = s.TrialSink;
        trial.Reset();
        WriteCflAlphas(trial, s.Cdf, alphaU, alphaV);
        long signalingCost = Av1RdCost.CombineCost(0, trial.Bits, s.Lambda);

        return costU + costV + signalingCost;
    }

    /// <summary>One plane's real, trial-costed CFL alpha search -- see <see cref="TryCflCandidate"/>'s remarks.</summary>
    private static long TryCflPlane(TileState s, int[] reconPlane, int[] source, Av1CoefficientWriter.PlaneContext ctx, int cx, int cy, int chromaSizePixels, int log2Size, bool availL, bool availU, bool haveAboveRight, bool haveBelowLeft, bool filterTypeSmooth, Av1EdgeArray above, Av1EdgeArray left, int[] pred, int[] lumaAc, long lumaAvg, out int bestAlpha)
    {
        Av1IntraPrediction.BuildEdges(above, left, reconPlane, s.ChromaWidth, cx, cy, chromaSizePixels, chromaSizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);
        Av1IntraPrediction.Predict(pred, chromaSizePixels, chromaSizePixels, log2Size, log2Size, above, left, Av1IntraMode.DcPred, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta: 0, enableIntraEdgeFilter: true, filterTypeSmooth, s.ChromaWidth - 1, s.ChromaHeight - 1, cx, cy, bitDepth: 8);

        // DC_PRED produces one constant value across the whole predicted block, so a single sample stands in
        // for the entire buffer -- avoids needing a second scratch buffer to remember the DC baseline across
        // ApplyCflAlpha's per-candidate in-place overwrites of `pred`.
        int dcConstant = pred[0];

        long numerator = 0;
        long denominator = 0;
        int total = chromaSizePixels * chromaSizePixels;
        for (int i = 0; i < chromaSizePixels; i++)
        {
            int rowBase = ((cy + i) * s.ChromaWidth) + cx;
            int localRowBase = i * chromaSizePixels;
            for (int j = 0; j < chromaSizePixels; j++)
            {
                long ac = lumaAc[localRowBase + j] - lumaAvg;
                numerator += ac * (source[rowBase + j] - dcConstant);
                denominator += ac * ac;
            }
        }

        bestAlpha = 0;
        if (denominator == 0)
        {
            // This block's luma has zero AC variance (perfectly flat) -- CFL has nothing to predict from,
            // regardless of what the chroma content looks like.
            return long.MaxValue;
        }

        int alphaEstimate = (int)Math.Clamp(Math.Round(numerator * 64.0 / denominator), -16, 16);
        int windowLo = Math.Max(-16, alphaEstimate - 1);
        int windowHi = Math.Min(16, alphaEstimate + 1);

        long bestCost = long.MaxValue;
        for (int alpha = windowLo; alpha <= windowHi; alpha++)
        {
            if (alpha == 0)
            {
                continue; // never spec-legal to signal alone (see TryCflCandidate) and never useful either
            }

            ApplyCflAlpha(pred, lumaAc, lumaAvg, total, dcConstant, alpha, bitDepth: 8);
            long cost = ComputeCandidateCost(s, source, s.ChromaWidth, pred, cx, cy, chromaSizePixels, ptype: 1, ctx);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestAlpha = alpha;
            }
        }

        return bestCost;
    }

    /// <summary>
    /// Luma AC term for CFL (spec §7.11.5's <c>predict_chroma_from_luma</c>, the part shared across every
    /// alpha candidate -- luma content doesn't depend on alpha, only how it's scaled and added to chroma's DC
    /// does). Ported from <see cref="Av1IntraPrediction.PredictChromaFromLuma"/>'s own identical loop, not a
    /// re-derivation: same subsample-and-average-then-Q3-scale computation, same rounding.
    ///
    /// <para><paramref name="lumaPlane"/> is always <see cref="TileState.ReconY"/> in practice, but taken as
    /// a parameter rather than hardcoded so this method doesn't need to know which caller it's serving. Real,
    /// already-reconstructed luma matters here: <see cref="SearchUvMode"/>'s CFL candidate only runs after
    /// <see cref="EncodeLeaf"/>'s early, pre-search luma reconstruction (see its remarks) has already written
    /// this leaf's own real <see cref="TileState.ReconY"/> data, and the real, final commit
    /// (<see cref="EncodeChromaRegion"/>/<see cref="EncodeNonLosslessLargeChromaRegion"/>) reads the same,
    /// by-then-unquestionably-real buffer -- both callers see identical data, eliminating the search/commit
    /// mismatch an earlier version of this method had when the search used <see cref="TileState.SourceY"/>
    /// as a stand-in instead.</para>
    /// </summary>
    private static long ComputeCflLumaAc(int[] lumaPlane, int lumaStride, int lumaX, int lumaY, int chromaSizePixels, int log2Size, int subX, int[] lumaAcOut)
    {
        int subY = subX; // this encoder's chroma subsampling is always symmetric (4:2:0 or 4:4:4)
        int maxLumaW = lumaX + (chromaSizePixels << subX);
        int maxLumaH = lumaY + (chromaSizePixels << subY);

        long lumaAvg = 0;
        for (int i = 0; i < chromaSizePixels; i++)
        {
            int lumaRow = Math.Min(lumaY + (i << subY), maxLumaH - (1 << subY));
            for (int j = 0; j < chromaSizePixels; j++)
            {
                int lumaCol = Math.Min(lumaX + (j << subX), maxLumaW - (1 << subX));

                int t = 0;
                for (int dy = 0; dy <= subY; dy++)
                {
                    for (int dx = 0; dx <= subX; dx++)
                    {
                        t += lumaPlane[((lumaRow + dy) * lumaStride) + lumaCol + dx];
                    }
                }

                int v = t << (3 - subX - subY);
                lumaAcOut[(i * chromaSizePixels) + j] = v;
                lumaAvg += v;
            }
        }

        return Round2(lumaAvg, log2Size + log2Size);
    }

    /// <summary>Applies a candidate CFL alpha on top of an already-DC-predicted <paramref name="pred"/>, matching <see cref="Av1IntraPrediction.PredictChromaFromLuma"/>'s exact formula (spec §7.11.5).</summary>
    private static void ApplyCflAlpha(int[] pred, int[] lumaAc, long lumaAvg, int total, int dcConstant, int alpha, int bitDepth)
    {
        for (int i = 0; i < total; i++)
        {
            long ac = lumaAc[i] - lumaAvg;
            int scaledLuma = Round2Signed(alpha * ac, 6);
            pred[i] = Clip1(dcConstant + scaledLuma, bitDepth);
        }
    }

    /// <summary>
    /// Write-side mirror of <c>Av1TileDecoder.ReadCflAlphas</c> (spec §5.11.45's write direction) -- an
    /// <see cref="IAv1SymbolSink"/> parameter so the same call trial-costs (<see cref="Av1TrialSymbolSink"/>,
    /// via <see cref="TryCflCandidate"/>) or really writes (<see cref="Av1SymbolEncoder"/>, via
    /// <see cref="EncodeLeaf"/>) identically, matching this file's existing <see cref="Av1CoefficientWriter.WriteCoeffs"/>
    /// convention. <paramref name="alphaU"/>/<paramref name="alphaV"/> are the real signed alpha values
    /// (spec range roughly ±1..16, never both zero -- see <see cref="TryCflCandidate"/>'s remarks), not the
    /// bitstream symbols themselves; this derives cflAlphaSigns/cflAlphaU/cflAlphaV the same way
    /// <c>ReadCflAlphas</c> reconstructs alphaU/alphaV from them, just in reverse.
    /// </summary>
    private static void WriteCflAlphas(IAv1SymbolSink sink, Av1CdfContext cdf, int alphaU, int alphaV)
    {
        int signU = alphaU == 0 ? 0 : alphaU < 0 ? 1 : 2;
        int signV = alphaV == 0 ? 0 : alphaV < 0 ? 1 : 2;

        // Inverse of ReadCflAlphas's `signU = (cflAlphaSigns+1)/3; signV = (cflAlphaSigns+1)%3` -- a bijection
        // over cflAlphaSigns in [0,7] onto every (signU, signV) pair except (0, 0), which TryCflCandidate's
        // caller never passes here.
        int cflAlphaSigns = (signU * 3) + signV - 1;
        sink.WriteSymbol(cdf.CflSign, cflAlphaSigns);

        if (signU != 0)
        {
            int ctx = ((signU - 1) * 3) + signV;
            sink.WriteSymbol(cdf.CflAlpha[ctx], Math.Abs(alphaU) - 1);
        }

        if (signV != 0)
        {
            int ctx = ((signV - 1) * 3) + signU;
            sink.WriteSymbol(cdf.CflAlpha[ctx], Math.Abs(alphaV) - 1);
        }
    }

    /// <summary><c>Round2</c> (spec §4.7). Duplicated from <see cref="Av1IntraPrediction"/>'s own private identically-named helper rather than widening that method's visibility, to keep this encoder-only CFL search self-contained.</summary>
    private static int Round2(long x, int n) => n == 0 ? (int)x : (int)((x + (1L << (n - 1))) >> n);

    /// <summary><c>Round2Signed</c> (spec §4.7). See <see cref="Round2"/>'s remarks.</summary>
    private static int Round2Signed(long x, int n) => x >= 0 ? Round2(x, n) : -Round2(-x, n);

    /// <summary><c>Clip1</c> (spec §4.10.6). See <see cref="Round2"/>'s remarks.</summary>
    private static int Clip1(int x, int bitDepth) => Math.Clamp(x, 0, (1 << bitDepth) - 1);

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
        int subBlockMiRow = r & s.SbMiMask;
        int subBlockMiCol = c & s.SbMiMask;
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

        // Below Block8x8 (sizeMi == 1), spec's intra_angle_info_y() (§5.11.42) never reads an angle_delta
        // symbol and the decoder always reconstructs with angleDelta == 0 regardless of mode -- see
        // EstimateLumaCost's identical angleDeltaAllowed remarks for why the search itself, not just the
        // write site below, has to respect this for real (not just estimated) leaves too.
        bool angleDeltaAllowed = sizeMi >= 2;

        foreach (int mode in CandidateModes)
        {
            bool directional = Av1IntraMode.IsDirectional(mode) && angleDeltaAllowed;
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
                long cost;
                bool overSized = sizePixels > 64;
                if (overSized)
                {
                    // See ComputeLosslessWholeLeafCostPerSubBlock's remarks: real AV1 intra prediction never
                    // spans more than 64x64 in one shot, only reachable for a lossless 128x128 leaf. Real
                    // boundary availability here (unlike EstimateLumaCost's speculative version) since this
                    // is the real, already-size-committed mode decision, scored the same way
                    // EncodeLosslessLumaResidual's real per-sub-block residual commit will be.
                    cost = ComputeLosslessWholeLeafCostPerSubBlock(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, sizePixels, ptype: 0, s.YCoeffCtx, mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability: true);
                }
                else
                {
                    Av1IntraPrediction.BuildEdges(above, left, s.ReconY, s.YWidth, x, y, sizePixels, sizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);
                    Av1IntraPrediction.Predict(pred, sizePixels, sizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.YWidth - 1, s.YHeight - 1, x, y, bitDepth: 8);
                    cost = ComputeCandidateCost(s, s.SourceY, s.YWidth, pred, x, y, sizePixels, ptype: 0, s.YCoeffCtx);
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                    bestAngleDelta = angleDelta;

                    // bestPred only matters for the non-lossless whole-leaf commit below (gated
                    // !s.Lossless) -- never read for lossless (see EncodeLosslessLumaResidual's own real,
                    // separate per-4x4 prediction), and pred itself only ever holds one 4x4 sub-block's
                    // worth of data in the overSized branch, not a real whole-leaf snapshot to copy.
                    if (!overSized)
                    {
                        Array.Copy(pred, bestPred, leafElements);
                    }
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

                long cost = ComputeCandidateCost(s, s.SourceY, s.YWidth, pred, x, y, sizePixels, ptype: 0, s.YCoeffCtx);

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
        // lossless -- see Av1FrameHeaderWriter) and this leaf's size is palette-eligible
        // (Av1TileDecoder.AllowPalette: Block8x8 through Block64x64 -- real AV1 simply doesn't define
        // palette's block-size-context CDF (PaletteYMode/PaletteYSize) beyond 64x64, spec's own
        // PALETTE_BLOCK_SIZE_CONTEXTS == 7 covering exactly that range, so a coding block above 64x64 --
        // reachable now via 128x128 superblocks -- must never read palette_mode_info() at all). Every leaf
        // this encoder produced before the partition floor reached 4x4 (sizeMi == 1) satisfied the lower size
        // gate unconditionally (floor was 8x8), but a genuine 4x4 leaf does not, and every leaf before
        // 128x128 superblocks satisfied the upper gate unconditionally too -- this must exactly match the
        // decoder's own gate: getting it wrong doesn't just miss a compression opportunity, it either
        // desyncs every bit after it (reading/writing a symbol the other side doesn't) or, as found here,
        // indexes PaletteYMode/PaletteYSize out of bounds outright.
        bool paletteStructurallyPresent = s.Lossless && sizeMi is >= 2 and <= 16;

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
        //
        // This used to be gated to sizeMi <= 2 (single-sub-block leaves only): EncodeIntrabcResidual
        // predicted a merged coding block's whole region in one Av1InterPrediction.PredictIntrabc call and
        // then sliced the result per 4x4 sub-block, which could desync from a real decoder's transform_block()
        // (spec §5.11.35, which calls PredictIntrabc fresh per sub-block from progressively-reconstructed
        // state) for a genuinely multi-sub-block IntraBC block. EncodeIntrabcResidual now predicts per
        // sub-block the same way -- see its remarks for detail. IntraBC's *exact*-match path (intrabcExact
        // above) never had this problem, since it has no per-sub-block prediction step at all.
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

        // Real cost-based uv_mode/uv_angle_delta search (see SearchUvMode's remarks) -- replaces the old
        // hardcoded DC_PRED, for both lossless and non-lossless chroma, at every leaf size. Non-lossless
        // chroma's transform type is mode-dependent (spec's Mode_To_Txfm, Av1TxTypeTables.ModeToTxfm --
        // Av1TileDecoder.ComputeTxType derives ADST/mixed transforms for every uv_mode except DC_PRED), which
        // used to make a real search unsafe here whenever the chroma region grew past one 4x4 transform
        // (sizeMi > 2 -- a 16x16/32x32 luma leaf's 4:2:0 chroma region, 8x8/16x16): Av1ForwardTransform's
        // forward ADST operators only existed at size 4 until this encoder's tx_type search phase generalized
        // them to 4/8/16 (every chroma region size this encoder ever produces -- 4:2:0's largest, from a
        // 32x32 luma leaf, is 16x16), so any uv_mode whose ModeToTxfm entry wasn't DCT_DCT would have had
        // nothing to forward-transform with at those larger sizes. Every ModeToTxfm entry is one of
        // DCT_DCT/AdstDct/DctAdst/AdstAdst (never IDTX or a 1D-only type), so once those operators covered
        // every real chroma size, nothing about the search itself needed to change to become safe at every
        // size -- see EncodeChromaRegion's own remarks for the write-side half of this. Lossless never had
        // this constraint: AV1 forces TX_4X4/WHT unconditionally at coded-lossless regardless of prediction
        // mode (ComputeTxType's own lossless short-circuit), so uv_mode has no bearing on transform choice
        // there.
        //
        // Still skipped when neither neighbor is available (the frame's very first leaf) or !hasChroma/
        // usedIntrabc (spec's use_intrabc branch replaces yMode/uv_mode signaling entirely -- see the
        // intrabc branch below, which leaves uv_mode at its DC_PRED default instead). With no real edge
        // data on either side, every candidate predicts from the same synthetic default fill, so a
        // directional mode/angle_delta search there is pure overhead with no possible benefit -- lossless
        // still reconstructs bit-exactly regardless (residual always corrects the rest of the way), but
        // choosing DC_PRED avoids gambling extra angle_delta signaling bits on a block with no real content
        // to base a directional choice on.
        // Non-lossless luma's real transform/quantize/trellis/reconstruct, computed here -- before
        // SearchUvMode runs, not at this leaf's later bitstream-order commit position below -- so CFL's
        // search (TryCflCandidate) has this leaf's own real reconstructed luma to read from
        // TileState.ReconY, matching what a real decoder will actually have available by the equivalent
        // point in its own decode. uv_mode/cfl_alphas are decided (and, per spec, their bitstream position
        // precedes) before luma's own residual is ever written, but nothing requires the ENCODER's internal
        // pixel reconstruction to wait that long too -- only WriteCoeffs (the actual bitstream symbol write,
        // which must stay at its original position to preserve mode_info()-before-residual() ordering) is
        // deferred; TileState.LumaLevels and bestTxType (declared here, at a scope enclosing both this block
        // and the later commit site) carry the result forward for that deferred write to reuse instead of
        // recomputing.
        //
        // Always run when reached (not conditioned on whether this leaf's Y palette will end up used --
        // that isn't decided until after this point, see the palette eligibility checks below), so it can't
        // avoid the tx_type search/transform/quantize/trellis cost purely by knowing palette will win --
        // real, if occasionally wasted, work is far simpler and safer than also hoisting palette's own
        // decision earlier just to skip it. A leaf whose Y palette does win later overwrites TileState.ReconY
        // with the palette's own (exact, source-copied) reconstruction instead -- see that branch, unchanged,
        // below.
        int bestTxType = Av1TxType.DctDct;
        if (!s.Lossless && !usedIntrabc)
        {
            int[] earlyResidual = s.Residual;
            int leafElementCount = sizePixels * sizePixels;
            long earlySse = 0;
            for (int i = 0; i < leafElementCount; i++)
            {
                int diff = s.SourceY[((y + (i / sizePixels)) * s.YWidth) + x + (i % sizePixels)] - bestPred[i];
                earlyResidual[i] = diff;
                earlySse += (long)diff * diff;
            }

            if (sizePixels < 32)
            {
                long bestTxTypeCost = long.MaxValue;
                foreach (int candidateTxType in Av1TxTypeTables.TxTypeIntraInvSet2)
                {
                    long txTypeCost = ComputeTxTypeCost(s, earlyResidual, earlySse, sizePixels, ptype: 0, x, y, s.YCoeffCtx, candidateTxType);
                    if (txTypeCost < bestTxTypeCost)
                    {
                        bestTxTypeCost = txTypeCost;
                        bestTxType = candidateTxType;
                    }
                }
            }

            int[] earlyCoeff = s.Coeff;
            Av1ForwardTransform.Forward2D(earlyResidual, earlyCoeff, sizePixels, bestTxType);
            Av1ForwardQuantizer.Quantize(earlyCoeff, s.LumaLevels, sizePixels, s.BaseQIdx);
            OptimizeCoeffTrellis(s, earlyCoeff, s.LumaLevels, sizePixels, ptype: 0, c, r, s.YCoeffCtx);

            // Write the prediction into the reconstruction buffer before Reconstruct() adds the residual --
            // matches Av1TileDecoder's own predict-then-reconstruct-in-place ordering.
            for (int i = 0; i < sizePixels; i++)
            {
                Array.Copy(bestPred, i * sizePixels, s.ReconY, ((y + i) * s.YWidth) + x, sizePixels);
            }

            Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, x, y, sizePixels, s.LumaLevels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: false, bestTxType);
        }

        int bestUvMode = Av1IntraMode.DcPred;
        int bestUvAngleDelta = 0;
        int bestAlphaU = 0;
        int bestAlphaV = 0;
        if (hasChroma && !usedIntrabc && (availU || availL))
        {
            (bestUvMode, bestUvAngleDelta, bestAlphaU, bestAlphaV) = SearchUvMode(s, r, c, x, y, sizeMi, availU, availL);
        }

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
        // (palette_mode_info()'s UV branch only depends on uv_mode == DC_PRED -- now that uv_mode is really
        // searched instead of hardcoded, this must gate on the search's result, not assume it's always
        // DC_PRED). This encoder still only ever *uses* palette all-or-nothing (both Y and, when
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
        bool uvPaletteEligible = !usedIntrabc && paletteStructurallyPresent && hasChroma && bestUvMode == Av1IntraMode.DcPred
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
            if (hasChroma)
            {
                MarkChromaBlockDecoded(s, r, c, sizeMi);
            }
        }
        else
        {
        s.Symbols.WriteSymbol(s.Cdf.IntraFrameYMode[yModeCtx0][yModeCtx1], bestMode);

        // intra_angle_info_y() (spec §5.11.42): structurally present only when this leaf's size is >= 8x8
        // (Av1TileDecoder.IntraAngleInfoY's own _miSize >= Block8x8 gate) -- no longer always true now that
        // the partition floor reaches 4x4 (sizeMi == 1), so this has to check for real; the search above
        // already never picks a nonzero bestAngleDelta for such a leaf (angleDeltaAllowed), so this is a
        // structural-presence match, not a search-quality change.
        if (sizeMi >= 2 && Av1IntraMode.IsDirectional(bestMode))
        {
            s.Symbols.WriteSymbol(s.Cdf.AngleDelta[bestMode - Av1IntraMode.VPred], bestAngleDelta + MaxAngleDelta);
        }

        if (hasChroma)
        {
            // uv_mode is always signalled when hasChroma, now a real cost-searched mode (see
            // SearchUvMode) instead of hardcoded DC_PRED -- but which CDF table depends on cflAllowed
            // (spec §8.3.2, mirroring Av1TileDecoder.ReadUvMode exactly): non-lossless, this encoder's
            // leaf (always 8x8, forced -- see EncodePartitionForced) always has cflAllowed == true (block
            // size <= 32). Lossless is size-dependent instead: GetPlaneResidualSize(bSize, plane:1, ...)
            // is Block4x4 at 4:2:0 (chroma always coded as one 4x4 sub-block per luma 4x4, matching luma
            // 1:1 -- see EncodeChromaRegion) for any leaf size, but equals the leaf's own luma block size
            // at 4:4:4 (chroma matches luma's leaf size 1:1 there too), so cflAllowed flips to false for
            // lossless + 4:4:4 -- except at the 4x4 leaf floor itself, where the leaf's own luma block size
            // already equals Block4x4, so this same condition flips back to true there. Getting this CDF
            // wrong doesn't just compress worse -- it silently desyncs the entropy decoder against any real
            // AV1 decoder, since CFL-allowed-ness
            // picks which adaptive probability table the very next symbol is read from. Chroma444 and
            // non-lossless never co-occur in this encoder (see Av1FrameEncoder.Encode's chroma444 gate),
            // so the non-lossless branch never needs to consult it. This condition was already exactly right
            // before CFL (Phase 6 backlog item) was real-searched -- SearchUvMode never picked
            // Av1IntraMode.UvCflPred before, but the CDF-selection logic here has to match the decoder's
            // is_cfl_allowed() regardless of whether CFL is ever actually chosen, since it's read
            // unconditionally whenever hasChroma.
            bool cflAllowed = s.Lossless
                ? Av1BlockTables.GetPlaneResidualSize(bSize, 1, !s.Chroma444, !s.Chroma444) == Av1BlockSize.Block4x4
                : true;
            var uvModeCdf = cflAllowed ? s.Cdf.UvModeCflAllowed[bestMode] : s.Cdf.UvModeCflNotAllowed[bestMode];
            s.Symbols.WriteSymbol(uvModeCdf, bestUvMode);

            // read_cfl_alphas() (spec §5.11.45): structurally present, immediately after uv_mode and before
            // intra_angle_info_uv(), whenever uv_mode == UV_CFL_PRED (Av1TileDecoder's own read order at
            // ReadUvMode()/ReadCflAlphas()/IntraAngleInfoUv -- bitstream position matters here, not just
            // logical presence). SearchUvMode never returns UvCflPred unless cflAllowed was true for this
            // leaf (see its own remarks), so this can't fire from a CDF table that didn't structurally offer
            // the symbol in the first place.
            if (bestUvMode == Av1IntraMode.UvCflPred)
            {
                WriteCflAlphas(s.Symbols, s.Cdf, bestAlphaU, bestAlphaV);
            }

            // intra_angle_info_uv() (spec §5.11.43): structurally present whenever this leaf's size is
            // >= 8x8 (no longer always true now that the partition floor reaches 4x4, sizeMi == 1) and the
            // searched uv_mode is directional, mirroring Av1TileDecoder.IntraAngleInfoUv exactly, including
            // its shared AngleDelta CDF table (indexed by mode class, not by plane). SearchUvMode's own
            // angleDeltaAllowed gate already never picks a nonzero bestUvAngleDelta for such a leaf.
            // UV_CFL_PRED is never directional (Av1IntraMode.IsDirectional(UvCflPred) is false), so this
            // never fires for a CFL leaf -- no angle_delta symbol competes with cfl_alphas for this leaf.
            if (sizeMi >= 2 && Av1IntraMode.IsDirectional(bestUvMode))
            {
                s.Symbols.WriteSymbol(s.Cdf.AngleDelta[bestUvMode - Av1IntraMode.VPred], bestUvAngleDelta + MaxAngleDelta);
            }
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

            // has_palette_uv is only structurally present when uv_mode == DC_PRED (spec's
            // palette_mode_info(), mirrored from Av1TileDecoder.PaletteModeInfo's identical
            // `_hasChroma && _uvMode == Av1IntraMode.DcPred` gate) -- now that uv_mode is really searched,
            // this can no longer assume it's always DC_PRED.
            if (hasChroma && bestUvMode == Av1IntraMode.DcPred)
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
            if (hasChroma)
            {
                MarkChromaBlockDecoded(s, r, c, sizeMi);
            }
        }
        else
        {
            if (s.Lossless && sizePixels > 64)
            {
                // Spec's real residual() (§5.11.34) never iterates a coding block bigger than 64x64 as one
                // flat raster scan of transform blocks -- it chunks it into 64x64-or-smaller pieces (Av1TileDecoder.Residual's
                // widthChunks/heightChunks) and, for EACH chunk, writes every plane (luma, then U, then V)
                // before moving to the next chunk. Only reachable for a lossless 128x128 leaf (this
                // encoder's non-lossless leaves never exceed 32x32), so this only ever chunks into exactly 4
                // 64x64 quadrants (2x2). Confirmed by direct encoder/decoder cross-instrumentation: the
                // decoder's own x4/y4 sequence wraps at 16 (64 pixels), not 32, proving this chunk-major,
                // plane-minor order is what a real decoder actually expects -- the previous flat "all luma
                // sub-blocks for the whole leaf, then all chroma" order desynced the entropy stream the
                // moment a genuine >64x64 leaf was ever chosen.
                int chunksPerSide = sizePixels / 64;
                for (int chunkY = 0; chunkY < chunksPerSide; chunkY++)
                {
                    for (int chunkX = 0; chunkX < chunksPerSide; chunkX++)
                    {
                        int chunkR = r + (chunkY * 16);
                        int chunkC = c + (chunkX * 16);
                        int chunkX_px = x + (chunkX * 64);
                        int chunkY_px = y + (chunkY * 64);

                        EncodeLosslessLumaResidual(s, chunkR, chunkC, chunkX_px, chunkY_px, bestMode, bestAngleDelta, filterTypeSmooth, bestUseFilterIntra, bestFilterIntraMode, blockSize: 64);

                        if (hasChroma && !usedIntrabc)
                        {
                            EncodeChromaRegion(s, chunkR, chunkC, chunkX_px, chunkY_px, sizeMi: 16, bestUvMode, bestUvAngleDelta, bestAlphaU, bestAlphaV);
                        }
                    }
                }
            }
            else if (s.Lossless)
            {
                // AV1 forces TX_4X4 for every block when lossless -- the leaf's transform splits into
                // (sizePixels/4)^2 4x4 sub-blocks, each with its own predict-then-reconstruct pass (see the
                // method's remarks on why this can't just reuse bestPred/the whole-leaf residual the
                // non-lossless path below does).
                EncodeLosslessLumaResidual(s, r, c, x, y, bestMode, bestAngleDelta, filterTypeSmooth, bestUseFilterIntra, bestFilterIntraMode, sizePixels);
            }
            else
            {
                // sizePixels is 8, 16, or 32 -- EncodePartitionForced/DecidePartition's non-lossless floor is
                // 8x8 (sizeMi == 2), and this encoder never keeps a non-lossless leaf above 32x32 as one
                // (EncodePartitionForced's superblock traversal always splits a 64x64 node at least once
                // before DecidePartition's floor logic ever runs on it -- see the project plan's
                // partition/TX-size RDO phase). tx_mode stays TX_MODE_LARGEST (Av1FrameHeaderWriter never
                // signals tx_mode_select), so the transform size here is always exactly the coding block's
                // own size -- no separate tx_size symbol to write, unlike a TX_MODE_SELECT encoder would need.
                //
                // The real transform/quantize/trellis/reconstruct work (Phase 4's tx_type search included)
                // already ran early, before SearchUvMode -- see that call site's remarks for why (CFL needs
                // this leaf's own real reconstructed luma before it can be searched at all, which is earlier
                // than this leaf's residual would otherwise be committed). TileState.LumaLevels and
                // bestTxType (both from that earlier scope) are this branch's only remaining inputs; only
                // WriteCoeffs (the actual bitstream write) waits until here, matching spec's
                // mode_info()-before-residual() ordering.
                int[] levels = s.LumaLevels;

                int txSz = Av1ForwardTransform.SizeToTxSz(sizePixels);
                int txSzSqr = Av1CoeffTables.TxSizeSqr[txSz];

                // intraDir: FilterIntraModeToIntraDir[filterIntraMode] when this leaf used FILTER_INTRA
                // (spec's transform_type() context derivation, mirrored from Av1TileDecoder.TransformType --
                // bestMode is always DC_PRED whenever bestUseFilterIntra is true, per the search above, but
                // that's not the same context index unless filterIntraMode itself also happens to map back
                // to DC_PRED).
                int intraDir = bestUseFilterIntra ? Av1TxTypeTables.FilterIntraModeToIntraDir[bestFilterIntraMode] : bestMode;

                // Y transform type symbol: writes whichever type the search above actually picked (the
                // CDF-inverse lookup mirrors Av1TileDecoder.TransformType's own TxTypeIntraInvSet2 read,
                // Av1TileDecoder.cs's TransformType) -- but Av1TileDecoder.GetTxSet forces TX_SET_DCTONLY (no
                // tx_type symbol read at all) whenever txSzSqrUp == TX_32X32, i.e. exactly a 32x32 leaf here
                // (this encoder's leaves are always square, so txSzSqrUp == txSz) -- bestTxType is left at
                // its DctDct default there (the search above never ran) and, correctly, no symbol is written
                // for it either. Passing null for writeLumaTxType at that size (rather than writing a symbol
                // no real decoder expects) is required for correctness, not just an optimization -- writing
                // it would desync the entropy stream from here on. Only actually invoked by WriteCoeffs when
                // the block turns out non-all-zero either way -- see its remarks.
                int txTypeSymbol = Array.IndexOf(Av1TxTypeTables.TxTypeIntraInvSet2, bestTxType);
                void WriteLumaTxType() => s.Symbols.WriteSymbol(s.Cdf.IntraTxTypeSet2[txSzSqr][intraDir], txTypeSymbol);
                Action? writeLumaTxType = sizePixels < 32 ? WriteLumaTxType : null;

                // WriteCoeffs takes (x4, y4) -- AV1's convention is x4 = column, y4 = row -- so this is
                // (c, r), not (r, c). Passing them backwards is silently unobservable on any square coding
                // block grid (miCols == miRows, e.g. every image up to 64x64 after padding), since
                // PlaneContext's MaxX4/MaxY4 bounds are then identical too; it only breaks on a genuinely
                // non-square, multi-superblock frame, where the above/left context bookkeeping silently
                // stops updating past whichever axis is shorter in mi-units -- desyncing every block's
                // entropy context (and the whole rest of the tile with it) from exactly that point onward.
                Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, sizePixels, ptype: 0, c, r, s.YCoeffCtx, writeLumaTxType);
                MarkLumaBlockDecoded(s, r, c, sizeMi, sizeMi);
            }
        }
        }

        int leafYMode = usedIntrabc ? Av1IntraMode.DcPred : bestMode;
        int leafUvMode = usedIntrabc ? Av1IntraMode.DcPred : bestUvMode;
        for (int dy = 0; dy < sizeMi; dy++)
        {
            int rowIdx = (r + dy) * s.MiCols;
            for (int dx = 0; dx < sizeMi; dx++)
            {
                int idx = rowIdx + c + dx;
                s.YModes[idx] = leafYMode;
                s.UvModes[idx] = leafUvMode;
                s.MiSizes[idx] = bSize;
                // Must mirror the actual written skip bit (usedPalette || intrabcExact, see above -- not
                // usedIntrabc), which is 0 for an approximate-match IntraBC leaf (it carries a real residual).
                // Av1TileDecoder stores its own Skips-equivalent grid from the literally decoded skip bit
                // (_skips[idx] = _skip), so using usedIntrabc here diverges from the decoder's neighbor-skip
                // context for any later leaf bordering this one whenever intrabcApprox (not intrabcExact) is
                // what made usedIntrabc true -- silently latent while approximate-match IntraBC leaves were
                // rare (this encoder's original 8x8-floor rarely picked one), but common enough once
                // partition-tree RDO's larger leaves increase how often it's chosen to desync real content.
                s.Skips[idx] = usedPalette || intrabcExact;
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

        // sizePixels > 64 already wrote chroma inline, interleaved per-64x64-chunk with luma above (see that
        // branch's remarks on why real AV1 requires that interleaving, not a separate whole-leaf pass here).
        if (hasChroma && !usedPalette && !usedIntrabc && sizePixels <= 64)
        {
            EncodeChromaRegion(s, r, c, x, y, sizeMi, bestUvMode, bestUvAngleDelta, bestAlphaU, bestAlphaV);
        }

        if (s.Lossless)
        {
            RecordIntrabcHashEntry(s, r, c, sizeMi, hasChroma);
        }
    }

    /// <summary>
    /// Commits a rectangular (HORZ/VERT-split) coding block: <paramref name="wMi"/>x<paramref name="hMi"/>
    /// mi units, <paramref name="wMi"/> != <paramref name="hMi"/>. First increment of full AV1 partition-type
    /// support (spec §5.11.4 defines 10 types -- NONE/HORZ/VERT/SPLIT/HORZ_A/HORZ_B/VERT_A/VERT_B/HORZ_4/
    /// VERT_4; this project's encoder previously only ever wrote NONE/SPLIT, a pure square quadtree -- see
    /// <see cref="ComputeDecidePartition"/>'s Horz/Vert cost candidates for the RDO half of this feature).
    /// <see cref="Av1TileDecoder"/> already handles every partition type generally (it must, to decode
    /// bitstreams from any real encoder), so this needed no decoder changes at all -- confirmed by reading
    /// <c>Av1TileDecoder.DecodePartition</c>/<c>Av1BlockTables.PartitionSubsize</c>, both already fully
    /// populated for rectangular sizes.
    ///
    /// <para><b>Scope of this first increment</b> (deliberately narrower than <see cref="EncodeLeaf"/>'s
    /// square path, to land a correct, real win without the risk of generalizing that ~700-line function's
    /// every helper in one pass):</para>
    /// <list type="bullet">
    /// <item>Lossless only -- <see cref="ComputeDecidePartition"/> never offers Horz/Vert for non-lossless.</item>
    /// <item>DC_PRED only, for both luma and chroma -- no directional/angle_delta search, no CFL. A real
    /// search is a natural follow-up once this basic shape is proven correct; DC_PRED still lets a wide-short
    /// or tall-narrow region (e.g. a text stroke or a flat border -- exactly the shapes a square-only quadtree
    /// can't express directly) code more cheaply than forcing a bigger square (wasted residual) or a smaller
    /// square split (repeated per-leaf signaling overhead), and <see cref="ComputeDecidePartition"/>'s real
    /// cost comparison against None/Split means this is never a forced regression, only sometimes a missed
    /// win relative to a hypothetical directional search.</item>
    /// <item>No palette, no IntraBC -- both remain square-leaf-only for now. The has_palette_y/has_palette_uv
    /// and use_intrabc *bits* are still spec-required and written here (see below), just always with a zero/
    /// false value; a rectangular leaf's content is also never recorded as a future IntraBC copy source
    /// (<see cref="RecordIntrabcHashEntry"/> is deliberately not called here), since that indexing assumes a
    /// square source region throughout (<see cref="IsValidIntrabcSource"/>'s own <c>bw = bh = sizeMi * 4</c>).</item>
    /// <item>Never reached for a leaf bigger than 64x64 in either dimension -- <see cref="ComputeDecidePartition"/>
    /// only offers Horz/Vert for a parent at or below sizeMi 16, so this never needs the &gt;64px chunked
    /// residual shape <see cref="EncodeLeaf"/>'s square path handles separately, and never hits the 4:2:0
    /// <c>bw4==1</c>/<c>bh4==1</c> shared-chroma edge case either (lossless is always 4:4:4 -- see
    /// <see cref="Av1FrameEncoder.Encode"/>'s <c>chroma444</c> gate -- so this never needs
    /// <see cref="Av1TileDecoder"/>'s equivalent logic for that).</item>
    /// </list>
    ///
    /// <para>Bit order mirrors <see cref="EncodeLeaf"/>'s exactly (spec's own <c>mode_info()</c>/<c>residual()</c>
    /// ordering): skip, use_intrabc(=0), y_mode(=DC_PRED, no angle_delta since DC isn't directional), uv_mode
    /// (=DC_PRED, no cfl_alphas/angle_delta), palette_mode_info() (has_palette_y/uv, both =0, using the real
    /// spec <c>AllowPalette</c> gate generalized to a rectangular <c>bSize</c> -- see its own
    /// remarks below for why this can't reuse <see cref="EncodeLeaf"/>'s old sizeMi-based shortcut),
    /// filter_intra_mode_info() (=0, gated on the real spec <c>max(bw,bh) &lt;= 32</c>, not the old
    /// square-only <c>sizePixels &lt;= 32</c> shortcut), then real per-4x4-sub-block WHT residual for Y, U,
    /// V (predicted fresh per sub-block from progressively-updated <see cref="TileState.ReconY"/>/ReconU/
    /// ReconV, exactly like <see cref="EncodeLosslessLumaResidual"/>/<see cref="EncodeChromaRegion"/>'s own
    /// lossless paths -- required for correctness, not just style: an interior sub-block's own local DC
    /// average depends on its immediate neighbors, not the whole leaf's edges, so this can't be a single
    /// whole-region prediction).</para>
    ///
    /// <para>Uses <c>bSize</c> internally (the coding block's own <see cref="Av1BlockSize"/>, derived from
    /// <paramref name="wMi"/>/<paramref name="hMi"/> via <see cref="BlockSizeFromWidthHeightMi"/>) for every
    /// spec-defined size-dependent gate below.</para>
    /// </summary>
    private static void EncodeRectangularLeaf(TileState s, int r, int c, int wMi, int hMi)
    {
        int widthPixels = wMi * 4;
        int heightPixels = hMi * 4;
        int bSize = BlockSizeFromWidthHeightMi(wMi, hMi);
        bool availU = r > 0;
        bool availL = c > 0;
        int x = c * 4;
        int y = r * 4;
        bool hasChroma = !s.MonoChrome;

        int aboveYMode = availU ? s.YModes[((r - 1) * s.MiCols) + c] : Av1IntraMode.DcPred;
        int leftYMode = availL ? s.YModes[(r * s.MiCols) + c - 1] : Av1IntraMode.DcPred;
        int yModeCtx0 = Av1BlockTables.IntraModeContext[aboveYMode];
        int yModeCtx1 = Av1BlockTables.IntraModeContext[leftYMode];

        int skipCtx = 0;
        if (availU)
        {
            skipCtx += s.Skips[((r - 1) * s.MiCols) + c] ? 1 : 0;
        }

        if (availL)
        {
            skipCtx += s.Skips[(r * s.MiCols) + c - 1] ? 1 : 0;
        }

        // skip is always 0 here: this leaf never covers every plane exactly (no palette/IntraBC in this
        // first increment -- see the class-level remarks above), so it always carries a real residual.
        s.Symbols.WriteSymbol(s.Cdf.Skip[skipCtx], 0);

        // use_intrabc (spec §5.11.7): structurally present whenever the frame allows it (tied to lossless,
        // same as EncodeLeaf's intrabcStructurallyPresent), regardless of this leaf's shape -- always 0 here.
        if (s.Lossless)
        {
            s.Symbols.WriteSymbol(s.Cdf.Intrabc, 0);
        }

        s.Symbols.WriteSymbol(s.Cdf.IntraFrameYMode[yModeCtx0][yModeCtx1], Av1IntraMode.DcPred);

        // No angle_delta: DC_PRED isn't directional, matching spec's own intra_angle_info_y() gate.
        if (hasChroma)
        {
            // cflAllowed's CDF-selection matters even though uv_mode is always written DC_PRED here --
            // getting the *context* wrong silently desyncs a real decoder just as much as getting the value
            // wrong (see EncodeLeaf's identical remark on this same computation).
            bool cflAllowed = Av1BlockTables.GetPlaneResidualSize(bSize, 1, !s.Chroma444, !s.Chroma444) == Av1BlockSize.Block4x4;
            var uvModeCdf = cflAllowed ? s.Cdf.UvModeCflAllowed[Av1IntraMode.DcPred] : s.Cdf.UvModeCflNotAllowed[Av1IntraMode.DcPred];
            s.Symbols.WriteSymbol(uvModeCdf, Av1IntraMode.DcPred);

            // No cfl_alphas (uv_mode isn't UV_CFL_PRED), no intra_angle_info_uv (uv_mode isn't directional).
        }

        // palette_mode_info() (spec §5.11.46): structurally present using the *real* spec gate
        // (AllowPalette -- Av1TileDecoder.AllowPalette, generalized to any block shape, not the old
        // square-only "sizeMi is >= 2 and <= 16" shortcut EncodeLeaf's own paletteStructurallyPresent still
        // uses, which has no meaning for a leaf with no single sizeMi). Getting this gate wrong wouldn't
        // just miss a compression opportunity -- it would desync the entropy stream by omitting/adding a
        // bit a real decoder does the opposite of.
        bool paletteStructurallyPresent = s.Lossless
            && Av1BlockTables.BlockWidth(bSize) <= 64
            && Av1BlockTables.BlockHeight(bSize) <= 64
            && bSize >= Av1BlockSize.Block8x8;
        if (paletteStructurallyPresent)
        {
            int bsizeCtx = GetPaletteBsizeCtx(bSize);

            // bestMode is always DC_PRED here, matching palette_mode_info()'s own has_palette_y gate.
            int paletteModeCtx = GetPaletteModeCtx(s, r, c, availU, availL);
            s.Symbols.WriteSymbol(s.Cdf.PaletteYMode[bsizeCtx][paletteModeCtx], 0);

            // has_palette_uv is only structurally present when uv_mode == DC_PRED, which it always is here.
            if (hasChroma)
            {
                s.Symbols.WriteSymbol(s.Cdf.PaletteUvMode[0], 0);
            }
        }

        // filter_intra_mode_info() (spec §5.11.24): real spec gate is max(bw, bh) <= 32, not a single
        // sizePixels -- see Av1TileDecoder.FilterIntraModeInfo's identical Math.Max check. bestMode is always
        // DC_PRED and PaletteSizeY is always 0 here, matching the gate's other two conditions unconditionally.
        if (Math.Max(widthPixels, heightPixels) <= 32)
        {
            s.Symbols.WriteSymbol(s.Cdf.FilterIntra[bSize], 0);
        }

        // residual() (spec §5.11.34): real per-4x4 WHT residual, Y then U then V (plane-major, matching
        // spec's own residual() loop and EncodeChromaRegion's identical ordering) -- skip is always 0 here,
        // so this always runs for every plane, never the reset_block_context() shortcut.
        int nW = wMi;
        int nH = hMi;
        var above = new Av1EdgeArray(16);
        var left = new Av1EdgeArray(16);
        var pred = s.Pred;
        var residual = s.Residual;
        var coeff = s.Coeff;
        var levels = s.Levels;

        for (int dr = 0; dr < nH; dr++)
        {
            for (int dc = 0; dc < nW; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                int subR = r + dr;
                int subC = c + dc;
                bool subAvailU = subR > 0;
                bool subAvailL = subC > 0;

                int subBlockMiRow = subR & s.SbMiMask;
                int subBlockMiCol = subC & s.SbMiMask;
                bool haveAboveRight = GetBlockDecoded(s, 0, subBlockMiRow - 1, subBlockMiCol + 1);
                bool haveBelowLeft = GetBlockDecoded(s, 0, subBlockMiRow + 1, subBlockMiCol - 1);

                Av1IntraPrediction.BuildEdges(above, left, s.ReconY, s.YWidth, subX, subY, 4, 4, subAvailL, subAvailU, haveAboveRight, haveBelowLeft, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);
                Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, above, left, Av1IntraMode.DcPred, subAvailL, subAvailU, useFilterIntra: false, filterIntraMode: 0, angleDelta: 0, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.YWidth - 1, s.YHeight - 1, subX, subY, bitDepth: 8);

                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((subY + i) * s.YWidth) + subX;
                    int predRowBase = i * 4;
                    for (int j = 0; j < 4; j++)
                    {
                        residual[(i * 4) + j] = s.SourceY[rowBase + j] - pred[predRowBase + j];
                    }
                }

                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                for (int i = 0; i < 4; i++)
                {
                    Array.Copy(pred, i * 4, s.ReconY, ((subY + i) * s.YWidth) + subX, 4);
                }

                Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 0, subC, subR, s.YCoeffCtx, writeLumaTxType: null, blockSize: widthPixels, blockHeight: heightPixels);
                Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, subX, subY, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);
                SetBlockDecoded(s, 0, subBlockMiRow, subBlockMiCol, true);
            }
        }

        if (hasChroma)
        {
            // 4:4:4 always, for lossless (see the class-level remarks) -- chroma shares luma's exact
            // position/footprint 1:1, no subsampling shift anywhere in this loop.
            foreach (var (source, recon, ctx) in new[]
            {
                (s.SourceU!, s.ReconU!, s.UCoeffCtx!),
                (s.SourceV!, s.ReconV!, s.VCoeffCtx!),
            })
            {
                for (int dr = 0; dr < nH; dr++)
                {
                    for (int dc = 0; dc < nW; dc++)
                    {
                        int subX = x + (dc * 4);
                        int subY = y + (dr * 4);
                        int subR = r + dr;
                        int subC = c + dc;
                        bool subAvailU = subR > 0;
                        bool subAvailL = subC > 0;

                        int subBlockMiRow = subR & s.SbMiMask;
                        int subBlockMiCol = subC & s.SbMiMask;
                        bool haveAboveRight = GetBlockDecoded(s, 1, subBlockMiRow - 1, subBlockMiCol + 1);
                        bool haveBelowLeft = GetBlockDecoded(s, 1, subBlockMiRow + 1, subBlockMiCol - 1);

                        Av1IntraPrediction.BuildEdges(above, left, recon, s.ChromaWidth, subX, subY, 4, 4, subAvailL, subAvailU, haveAboveRight, haveBelowLeft, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);
                        Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, above, left, Av1IntraMode.DcPred, subAvailL, subAvailU, useFilterIntra: false, filterIntraMode: 0, angleDelta: 0, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.ChromaWidth - 1, s.ChromaHeight - 1, subX, subY, bitDepth: 8);

                        for (int i = 0; i < 4; i++)
                        {
                            int rowBase = ((subY + i) * s.ChromaWidth) + subX;
                            int predRowBase = i * 4;
                            for (int j = 0; j < 4; j++)
                            {
                                residual[(i * 4) + j] = source[rowBase + j] - pred[predRowBase + j];
                            }
                        }

                        Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                        Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                        for (int i = 0; i < 4; i++)
                        {
                            Array.Copy(pred, i * 4, recon, ((subY + i) * s.ChromaWidth) + subX, 4);
                        }

                        int chromaBlockSizeArg = (nW * nH > 1) ? widthPixels : 0;
                        int chromaBlockHeightArg = (nW * nH > 1) ? heightPixels : 0;
                        Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 1, subC, subR, ctx, writeLumaTxType: null, blockSize: chromaBlockSizeArg, blockHeight: chromaBlockHeightArg);
                        Av1LocalReconstructor.Reconstruct(recon, s.ChromaWidth, subX, subY, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);
                        SetBlockDecoded(s, 1, subBlockMiRow, subBlockMiCol, true);
                        SetBlockDecoded(s, 2, subBlockMiRow, subBlockMiCol, true);
                    }
                }
            }
        }

        // Leaf-state bookkeeping, mirroring EncodeLeaf's own identical loop -- neighbor context (yMode,
        // skip, palette sizes/colors, mv/is_inter) that later leaves' own writes read.
        for (int dy = 0; dy < hMi; dy++)
        {
            int rowIdx = (r + dy) * s.MiCols;
            for (int dx = 0; dx < wMi; dx++)
            {
                int idx = rowIdx + c + dx;
                s.YModes[idx] = Av1IntraMode.DcPred;
                s.UvModes[idx] = Av1IntraMode.DcPred;
                s.MiSizes[idx] = bSize;
                s.Skips[idx] = false;
                s.PaletteSizesY[idx] = 0;
                s.PaletteSizesUV[idx] = 0;
                s.IsInters[idx] = false;
                s.MvRowsGrid[idx] = 0;
                s.MvColsGrid[idx] = 0;
                s.Written[idx] = true;
            }
        }

        // Deliberately no RecordIntrabcHashEntry call -- see the class-level remarks on why a rectangular
        // leaf is never offered as a future IntraBC copy source in this first increment.
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
    /// <c>is_mv_valid</c>'s IntraBC region-reachability check (spec §6.10.25 / libaom's <c>av1_is_dv_valid</c>),
    /// specialized: this encoder is always single-tile (MiRowStart/MiColStart are always 0, MiRowEnd/MiColEnd
    /// are always MiRows/MiCols). The spec's <c>bw &lt; 8 &amp;&amp; subsampling_x</c> / <c>bh &lt; 8 &amp;&amp;
    /// subsampling_y</c> edge adjustments are omitted: every leaf this encoder ever produces is at least 8x8
    /// (see the class-level remarks), so they can never apply. Getting this wrong wouldn't break
    /// round-tripping through this project's own decoder (which doesn't enforce it at read time -- see
    /// Av1TileDecoder.ReadMv's remarks), only real-world conformance against a third-party decoder (dav1d,
    /// libaom) that does -- an AVIF whose only purpose is to round-trip through this project's own codec
    /// wouldn't need this at all, but real interop is the actual point of writing AVIF files, so it's
    /// implemented in full rather than approximated. IntraBC is only ever attempted under lossless (see
    /// <see cref="EncodeLeaf"/>'s <c>intrabcStructurallyPresent</c>), and lossless always uses 128x128
    /// superblocks since PR #80 (<c>Av1FrameEncoder.cs</c>'s <c>sbSizeMi = lossless ? 32 : 16</c>) -- see the
    /// <c>gradient</c> computation below for the one term that depends on this.
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

        // use_128x128_superblock is always true here -- IntraBC only ever runs under lossless, and lossless
        // always uses 128x128 superblocks (see this method's remarks above). Real AV1 (libaom's
        // av1_is_dv_valid) adds a further +1 to this gradient in exactly that case; this used to omit it
        // (stale from before lossless switched to 128x128 superblocks, when it was genuinely always 0 here),
        // under-widening the wavefront reachability bound for every lossless frame since.
        int gradient = 1 + IntrabcDelaySb64 + (s.Lossless ? 1 : 0);
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
        return ComputeCandidateCost(s, s.SourceY, s.YWidth, pred, x, y, sizePixels, ptype: 0, s.YCoeffCtx);
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
    /// Unlike whole-leaf intra prediction's own *search/estimate* step (which legitimately approximates by
    /// reading <see cref="TileState.SourceY"/> up front -- see <see cref="DecidePartition"/>'s remarks),
    /// this real residual encode predicts every plane fresh per 4x4 sub-block from the progressively-updated
    /// <see cref="TileState.ReconY"/>/<see cref="TileState.ReconU"/>/<see cref="TileState.ReconV"/>, exactly
    /// mirroring <c>Av1TileDecoder.TransformBlock</c>'s own per-sub-block <c>PredictIntrabc</c> call (spec
    /// §5.11.35) -- this is what makes the path correct for a merged (&gt;8x8) leaf, not just a
    /// single-sub-block one (see the now-removed <c>sizeMi &lt;= 2</c> gate's history at the call site below
    /// for why this mattered).
    /// </summary>
    private static void EncodeIntrabcResidual(TileState s, int r, int c, int x, int y, int mvRow, int mvCol, bool hasChroma, int sizePixels)
    {
        var pred = s.Pred;

        int n = sizePixels / 4;
        for (int dr = 0; dr < n; dr++)
        {
            for (int dc = 0; dc < n; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                int subR = r + dr;
                int subC = c + dc;

                // Predicted fresh per 4x4 sub-block from s.ReconY, which by now already carries this same
                // leaf's own earlier (raster-order) sub-blocks' reconstructed pixels -- matching
                // Av1TileDecoder.TransformBlock's per-sub-block PredictIntrabc call exactly (spec §5.11.35),
                // not a stale whole-leaf snapshot taken before any of this leaf's own pixels existed.
                // mvRow/mvCol are unchanged per call (the coding block's one DV); only startX/startY move.
                // The trailing 0, 0 are PredictIntrabc's own chroma-subsampling-shift parameters (always 0
                // for luma) -- passed positionally here, not as subX:/subY:, since this loop already has
                // locals named subX/subY for the sub-block's pixel position.
                Av1InterPrediction.PredictIntrabc(pred, s.ReconY, s.YWidth, subX, subY, 4, 4, mvRow, mvCol, 0, 0, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);

                var residual = s.Residual;
                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((subY + i) * s.YWidth) + subX;
                    int predRowBase = i * 4;
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
                    Array.Copy(pred, i * 4, s.ReconY, ((subY + i) * s.YWidth) + subX, 4);
                }

                Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 0, subC, subR, s.YCoeffCtx, writeLumaTxType: null, blockSize: sizePixels);
                Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, subX, subY, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);

                SetBlockDecoded(s, 0, subR & s.SbMiMask, subC & s.SbMiMask, true);
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

            for (int dr = 0; dr < chromaN; dr++)
            {
                for (int dc = 0; dc < chromaN; dc++)
                {
                    int subCx = cx + (dc * 4);
                    int subCy = cy + (dr * 4);
                    int chromaR4 = (s.Chroma444 ? r : r / 2) + dr;
                    int chromaC4 = (s.Chroma444 ? c : c / 2) + dc;

                    // Same fix as the luma loop above: predicted fresh per 4x4 sub-block from recon
                    // (progressively updated by this same leaf's own earlier sub-blocks), not once for the
                    // whole chroma region.
                    Av1InterPrediction.PredictIntrabc(cpred, recon, s.ChromaWidth, subCx, subCy, 4, 4, mvRow, mvCol, subXc, subXc, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);

                    var residual = s.Residual;
                    for (int i = 0; i < 4; i++)
                    {
                        int rowBase = ((subCy + i) * s.ChromaWidth) + subCx;
                        int predRowBase = i * 4;
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
                        Array.Copy(cpred, i * 4, recon, ((subCy + i) * s.ChromaWidth) + subCx, 4);
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
            // Num_4x4_Blocks_High[BLOCK_128X128 or BLOCK_64X64] (spec's own sbSize4 in this exact fallback,
            // mirrored from Av1TileDecoder.AssignMv's identical branch) -- IntraBC only ever runs under
            // lossless (see EncodeLeaf's intrabcStructurallyPresent remarks), and lossless has used 128x128
            // superblocks since PR #80 (Av1FrameEncoder.cs's sbSizeMi = lossless ? 32 : 16), so this must be
            // 32, not the pre-#80 64x64-only value of 16 this used to hardcode. Getting this wrong doesn't
            // just compress worse: since this fallback is the actual PredMv the decoder independently derives
            // too, a stale/mismatched constant here silently predicts a *different* Mv on each side once
            // diffMv is added back, corrupting every pixel this leaf's IntraBC block-copy reads from --
            // confirmed via direct encoder/decoder cross-instrumentation (encoder computed predMvRow=-512,
            // decoder independently computed predMvRow=-1024 for the same leaf).
            int sbSize4 = s.Lossless ? 32 : 16;
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
    /// a <c>sizeMi</c>-square grid at luma-identical (unhalved) coordinates. Uses <paramref name="uvMode"/>/
    /// <paramref name="uvAngleDelta"/> (real cost-searched, see <see cref="SearchUvMode"/> -- CFL isn't
    /// implemented, so <paramref name="uvMode"/> is never <see cref="Av1IntraMode.UvCflPred"/>), and follows
    /// <see cref="TileState.Lossless"/> per sub-block for WHT vs. DCT/ADST exactly like the single-sub-block
    /// case this generalizes did.
    ///
    /// <para>Non-lossless forward-transforms with <c>Av1TxTypeTables.ModeToTxfm[uvMode]</c> -- DCT_DCT for
    /// DC_PRED, one of Av1ForwardTransform's AdstDct/DctAdst/AdstAdst operators for every other mode -- the
    /// exact same table <c>Av1TileDecoder.ComputeTxType</c> uses to pick its inverse transform for a chroma
    /// block from <c>_uvMode</c> alone (chroma's <c>tx_type</c> is never itself bitstream-signalled, unlike
    /// luma's; see <c>writeLumaTxType: null</c> below). Getting this wrong -- forward-transforming with a
    /// different type than <c>ModeToTxfm[uvMode]</c> implies -- wouldn't just compress worse, it would
    /// silently desync the decoder's reconstruction from this leaf onward, since the decoder derives its
    /// inverse transform from the signalled <c>uv_mode</c> with no way to learn otherwise. Lossless instead
    /// always uses WHT (<see cref="Av1ForwardWht"/>), matching <c>ComputeTxType</c>'s own <c>_lossless</c>
    /// short-circuit to DCT_DCT/WHT regardless of <c>uv_mode</c> -- see <see cref="TileState.Lossless"/>'s
    /// remarks.</para>
    ///
    /// <para>Plane must be the outer loop and sub-block position the inner loop -- not the reverse -- to
    /// match spec §5.11.34 <c>residual()</c>'s own <c>for (plane ...) { for (y...) for (x...)
    /// transform_block() } }</c> nesting: a real decoder reads every one of U's transform blocks in this
    /// coding block before reading any of V's, so writing them interleaved by position would silently
    /// desync the entropy stream the moment there's more than one sub-block per plane (any leaf bigger than
    /// the previous fixed 8x8 grid).</para>
    /// </summary>
    private static void EncodeChromaRegion(TileState s, int r, int c, int x, int y, int sizeMi, int uvMode, int uvAngleDelta, int alphaU, int alphaV)
    {
        int uvTxType = Av1TxTypeTables.ModeToTxfm[uvMode];
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

        // haveAboveRight/haveBelowLeft/filterTypeSmooth now need to be real (see SearchUvMode's remarks on
        // why the previous hardcoded false/false and filterTypeSmooth: false were only ever safe while
        // uvMode was always DC_PRED): mult converts a chroma 4x4-unit offset back to luma mi units (each
        // chroma unit spans 2 luma mi units at 4:2:0, 1 at 4:4:4) so the per-sub-block position can be
        // masked into the same superblock-relative space GetBlockDecoded/SetBlockDecoded already use for
        // luma, then shifted down by subX to land in this plane's own (possibly halved) index space --
        // mirrors Av1TileDecoder.TransformBlock's row/col/subX derivation exactly (see that method's remarks).
        bool subsampled = !s.Chroma444;
        int subX = subsampled ? 1 : 0;
        int mult = subsampled ? 2 : 1;
        bool filterTypeSmooth = GetChromaFilterType(s, r, c, r > 0, c > 0, subsampled);

        // Non-lossless with more than one 4x4 worth of chroma (chromaN > 1, i.e. every 16x16/32x32 luma leaf
        // -- see the project plan's partition/TX-size RDO phase) codes ONE transform sized to the whole
        // chroma region instead of looping a grid of 4x4 sub-blocks: a real decoder's GetTxSizeForPlane
        // always derives chroma's transform as the single largest size that fits the residual region outside
        // lossless's own spec-forced TX_4X4, so looping 4x4 sub-blocks here (as the lossless path below
        // still correctly does -- WHT is genuinely forced to 4x4 regardless of leaf size) would write the
        // wrong number of symbols against the wrong scan table for anything a real decoder expects.
        // uvTxType (already resolved above from the real, searched uvMode -- see EncodeLeaf's remarks on why
        // that search is safe at every size now) is passed straight through to the one larger transform this
        // codes: every Av1TxTypeTables.ModeToTxfm entry is one of DCT_DCT/AdstDct/DctAdst/AdstAdst, and
        // Av1ForwardTransform supports all four at every size this branch ever sees (4:2:0's largest chroma
        // region, from a 32x32 luma leaf, is 16x16), so there's no size/type combination left here that
        // forward-transforming can't handle.
        if (!s.Lossless && chromaN > 1)
        {
            EncodeNonLosslessLargeChromaRegion(s, r, c, chromaR4Base, chromaC4Base, cxBase, cyBase, chromaBlockSizePixels, subX, filterTypeSmooth, uvMode, uvAngleDelta, uvTxType, alphaU, alphaV);
            return;
        }

        // Non-lossless (this branch never reaches here with s.Lossless -- see EncodeLeaf's remarks on why
        // CFL is scoped to the lossy path), chromaN == 1 always means this loop's single (dr, dc) == (0, 0)
        // iteration covers exactly this leaf's own luma extent (sizeMi == 2, an 8x8 luma leaf at 4:2:0's
        // fixed non-lossless partition floor -- see the class remarks), so (x, y) -- this leaf's own base
        // luma pixel position, already this method's own parameters -- is CFL's luma base directly, with no
        // per-sub-block offset to add.
        bool isCfl = uvMode == Av1IntraMode.UvCflPred;

        int planeIndex = 0;
        foreach (var (source, recon, ctx) in new[]
        {
            (s.SourceU!, s.ReconU!, s.UCoeffCtx!),
            (s.SourceV!, s.ReconV!, s.VCoeffCtx!),
        })
        {
            planeIndex++;

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

                    int subBlockChromaRow = ((r + (dr * mult)) & s.SbMiMask) >> subX;
                    int subBlockChromaCol = ((c + (dc * mult)) & s.SbMiMask) >> subX;
                    bool haveAboveRight = GetBlockDecoded(s, planeIndex, subBlockChromaRow - 1, subBlockChromaCol + 1);
                    bool haveBelowLeft = GetBlockDecoded(s, planeIndex, subBlockChromaRow + 1, subBlockChromaCol - 1);

                    var above = new Av1EdgeArray(16);
                    var left = new Av1EdgeArray(16);
                    Av1IntraPrediction.BuildEdges(above, left, recon, s.ChromaWidth, cx, cy, 4, 4, availL, availU, haveAboveRight, haveBelowLeft, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);

                    // Reuses the same tile-wide scratch buffers luma just finished using for this leaf --
                    // safe because luma's use of them is already fully consumed (WriteCoeffs/Reconstruct
                    // called for every luma sub-block) before this method runs.
                    var pred = s.Pred;

                    // CFL predicts through an ordinary DC_PRED baseline (predict_intra's own DC-with-
                    // UV_CFL_PRED-mapped-to-DC_PRED call, spec §7.11.2 -- mirrored from Av1TileDecoder's
                    // identical `mode = isCfl ? DcPred : uvMode` substitution), then adds the luma-derived AC
                    // term on top -- Predict() itself has no UV_CFL_PRED case to dispatch to.
                    Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, above, left, isCfl ? Av1IntraMode.DcPred : uvMode, availL, availU, useFilterIntra: false, filterIntraMode: 0, uvAngleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.ChromaWidth - 1, s.ChromaHeight - 1, cx, cy, bitDepth: 8);
                    if (isCfl)
                    {
                        int dcConstant = pred[0];
                        int alpha = planeIndex == 1 ? alphaU : alphaV;
                        long lumaAvg = ComputeCflLumaAc(s.ReconY, s.YWidth, x, y, 4, log2Size: 2, subX, s.CflLumaAc);
                        ApplyCflAlpha(pred, s.CflLumaAc, lumaAvg, total: 16, dcConstant, alpha, bitDepth: 8);
                    }

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
                        Av1ForwardTransform.Forward2D(residual.AsSpan(0, 16), coeff.AsSpan(0, 16), 4, uvTxType);
                    }

                    var levels = s.Levels;
                    Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);
                    if (!s.Lossless)
                    {
                        OptimizeCoeffTrellis(s, coeff, levels, 4, ptype: 1, chromaC4, chromaR4, ctx);
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        Array.Copy(pred, i * 4, recon, ((cy + i) * s.ChromaWidth) + cx, 4);
                    }

                    // (x4, y4) = (chromaC4, chromaR4), not (chromaR4, chromaC4) -- see EncodeLeaf's luma call
                    // site for why the argument order matters here (x4 = column, y4 = row) even though it's
                    // unobservable on any square/single-superblock chroma grid.
                    Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 1, chromaC4, chromaR4, ctx, writeLumaTxType: null, blockSize: blockSizeArg);
                    Av1LocalReconstructor.Reconstruct(recon, s.ChromaWidth, cx, cy, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, s.Lossless, uvTxType);
                    SetBlockDecoded(s, planeIndex, subBlockChromaRow, subBlockChromaCol, true);
                }
            }
        }
    }

    /// <summary>
    /// <see cref="EncodeChromaRegion"/>'s non-lossless, chromaN &gt; 1 branch: one DCT_DCT transform sized to
    /// <paramref name="chromaBlockSizePixels"/> per plane (U then V, matching spec's plane-outer
    /// <c>residual()</c> order -- see <see cref="EncodeChromaRegion"/>'s own remarks on why that nesting
    /// matters) instead of a grid of 4x4 sub-blocks -- structurally the same single-whole-block predict/
    /// transform/quantize/write/reconstruct sequence <see cref="EncodeLeaf"/>'s own (now size-generic)
    /// non-lossless luma path uses, just applied to one chroma plane at a time. <paramref name="uvMode"/>/
    /// <paramref name="uvAngleDelta"/> come from the same real mode/angle search <see cref="EncodeLeaf"/> now
    /// always runs for chroma (Phase 4's generalized <see cref="Av1ForwardTransform"/> made real ADST-mixed
    /// search safe at every chroma size this encoder produces, removing the old forced-DC_PRED restriction);
    /// unlike <see cref="EncodeLeaf"/>'s luma path there's still no tx_type symbol to conditionally write here,
    /// since chroma's tx_type is never itself bitstream-signalled (always derived from <c>uv_mode</c> via
    /// <c>Av1TxTypeTables.ModeToTxfm</c>, see <see cref="EncodeChromaRegion"/>'s remarks) -- <paramref
    /// name="uvTxType"/> is passed through purely to drive the actual transform math. No <c>blockSize</c>
    /// override for <see cref="Av1CoefficientWriter.WriteCoeffs"/> either, since the transform now always
    /// exactly equals the chroma coding block (the same "transform == coding block" shortcut
    /// <see cref="EncodeLeaf"/>'s own non-lossless call already relies on).
    /// </summary>
    private static void EncodeNonLosslessLargeChromaRegion(TileState s, int r, int c, int chromaR4, int chromaC4, int cx, int cy, int chromaBlockSizePixels, int subX, bool filterTypeSmooth, int uvMode, int uvAngleDelta, int uvTxType, int alphaU, int alphaV)
    {
        bool availU = chromaR4 > 0;
        bool availL = chromaC4 > 0;
        bool isCfl = uvMode == Av1IntraMode.UvCflPred;

        // Real luma pixel coords this region's local (0, 0) corresponds to -- cx/cy are already real
        // full-resolution chroma-plane coords (this encoder's mi-grid alignment guarantees cx << subX == the
        // exact real luma x with no rounding loss, the same invariant SearchUvMode's own TryCflCandidate call
        // relies on for its (x, y) luma base).
        int lumaX = cx << subX;
        int lumaY = cy << subX;

        // Unlike EncodeChromaRegion's own 4x4-sub-block loop (whose subBlockChromaRow/Col shifts per
        // sub-block via `mult`), there is exactly one sub-block here -- the whole chroma region -- so its
        // BlockDecoded position is just (r, c)'s own masked-and-shifted position, no per-iteration offset.
        int subBlockChromaRow = (r & s.SbMiMask) >> subX;
        int subBlockChromaCol = (c & s.SbMiMask) >> subX;

        // Scaled by the region's own width/height in chroma 4x4 units (matching SearchUvMode's identical
        // `chromaCol + chromaN`/`chromaRow + chromaN` computation) -- a fixed +-1 offset only happens to be
        // correct at chromaN == 1 (4x4 chroma), which never reaches this method (that size takes
        // EncodeChromaRegion's own 4x4 sub-block loop instead; this method only ever runs for chromaN > 1).
        // Getting this wrong doesn't matter for DC_PRED (which never reads the above-right/below-left corner
        // samples these flags gate), so it went unnoticed while uv_mode was forced to DC_PRED here -- but a
        // real directional/smooth mode reads those corners, and BuildEdges silently clamps/replicates instead
        // of reading real neighbor pixels whenever told a neighbor isn't available, so a wrong flag corrupts
        // the whole block's prediction, not just a few edge pixels.
        int chromaN = chromaBlockSizePixels / 4;

        int log2Size = Log2FromPixels(chromaBlockSizePixels);
        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);
        var pred = s.Pred;

        int planeIndex = 0;
        foreach (var (source, recon, ctx) in new[]
        {
            (s.SourceU!, s.ReconU!, s.UCoeffCtx!),
            (s.SourceV!, s.ReconV!, s.VCoeffCtx!),
        })
        {
            planeIndex++;

            bool haveAboveRight = GetBlockDecoded(s, planeIndex, subBlockChromaRow - 1, subBlockChromaCol + chromaN);
            bool haveBelowLeft = GetBlockDecoded(s, planeIndex, subBlockChromaRow + chromaN, subBlockChromaCol - 1);

            Av1IntraPrediction.BuildEdges(above, left, recon, s.ChromaWidth, cx, cy, chromaBlockSizePixels, chromaBlockSizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);

            // CFL predicts through an ordinary DC_PRED baseline (spec §7.11.2's DC-with-UV_CFL_PRED-mapped-
            // to-DC_PRED call), then adds the luma-derived AC term on top -- Predict() itself has no
            // UV_CFL_PRED case to dispatch to (see EncodeChromaRegion's identical substitution).
            Av1IntraPrediction.Predict(pred, chromaBlockSizePixels, chromaBlockSizePixels, log2Size, log2Size, above, left, isCfl ? Av1IntraMode.DcPred : uvMode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta: uvAngleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.ChromaWidth - 1, s.ChromaHeight - 1, cx, cy, bitDepth: 8);
            if (isCfl)
            {
                int dcConstant = pred[0];
                int alpha = planeIndex == 1 ? alphaU : alphaV;
                long lumaAvg = ComputeCflLumaAc(s.ReconY, s.YWidth, lumaX, lumaY, chromaBlockSizePixels, log2Size, subX, s.CflLumaAc);
                ApplyCflAlpha(pred, s.CflLumaAc, lumaAvg, total: chromaBlockSizePixels * chromaBlockSizePixels, dcConstant, alpha, bitDepth: 8);
            }

            var residual = s.Residual;
            for (int i = 0; i < chromaBlockSizePixels; i++)
            {
                int rowBase = ((cy + i) * s.ChromaWidth) + cx;
                int predRowBase = i * chromaBlockSizePixels;
                for (int j = 0; j < chromaBlockSizePixels; j++)
                {
                    residual[predRowBase + j] = source[rowBase + j] - pred[predRowBase + j];
                }
            }

            var coeff = s.Coeff;
            Av1ForwardTransform.Forward2D(residual, coeff, chromaBlockSizePixels, uvTxType);
            var levels = s.Levels;
            Av1ForwardQuantizer.Quantize(coeff, levels, chromaBlockSizePixels, s.BaseQIdx);
            OptimizeCoeffTrellis(s, coeff, levels, chromaBlockSizePixels, ptype: 1, chromaC4, chromaR4, ctx);

            for (int i = 0; i < chromaBlockSizePixels; i++)
            {
                Array.Copy(pred, i * chromaBlockSizePixels, recon, ((cy + i) * s.ChromaWidth) + cx, chromaBlockSizePixels);
            }

            Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, chromaBlockSizePixels, ptype: 1, chromaC4, chromaR4, ctx, writeLumaTxType: null);
            Av1LocalReconstructor.Reconstruct(recon, s.ChromaWidth, cx, cy, chromaBlockSizePixels, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: false, uvTxType);

            // Marks this whole chromaN x chromaN sub-block footprint decoded -- not just its own (top-left)
            // position -- mirroring Av1TileDecoder.TransformBlock's own `for (i < stepY) for (j < stepX))
            // SetBlockDecoded(...)` loop exactly (stepX/stepY there are this same transform's width/height in
            // chroma 4x4 units, i.e. chromaN here, since tx_mode is always TX_MODE_LARGEST -- see this
            // method's own remarks). A single-position mark left every OTHER position this region actually
            // covers (chromaN > 1 means more than one) permanently "not yet decoded" from a later leaf's own
            // haveAboveRight/haveBelowLeft query's point of view, even after this region's real reconstruction
            // finished -- silently disagreeing with a real decoder (which, per the loop above, correctly marks
            // the whole footprint) about whether a neighbor is available. Getting this wrong doesn't matter
            // for DC_PRED (which never reads the above-right/below-left corner samples these flags gate, see
            // this method's own remarks above) -- which is exactly why it went unnoticed through PR #75's own
            // real chroma mode search landing -- but a real directional/smooth (or CFL) mode reads those
            // corners, and BuildEdges silently clamps/replicates instead of reading the real neighbor pixel
            // whenever told a neighbor isn't available, corrupting the whole block's prediction from a stale
            // "not decoded yet" flag, not just a few edge pixels -- confirmed via direct encoder/decoder
            // instrumentation cross-check, not just inference: at one queried position, the encoder's own
            // BlockDecoded state said false while the real decoder, given the identical bitstream, said true
            // for the identical (plane, row, col) query.
            for (int i = 0; i < chromaN; i++)
            {
                for (int j = 0; j < chromaN; j++)
                {
                    SetBlockDecoded(s, planeIndex, subBlockChromaRow + i, subBlockChromaCol + j, true);
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
                int subBlockMiRow = subR & s.SbMiMask;
                int subBlockMiCol = subC & s.SbMiMask;
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

                // blockSize: the coding block's real pixel size -- >= 8 for every leaf bigger than the 4x4
                // floor (n > 1 sub-blocks, so the all_zero context can't take the transform-equals-block
                // shortcut, see WriteCoeffs's remarks), but exactly 4 for a genuine 4x4 leaf (n == 1), where
                // it deliberately *does* take that shortcut -- WriteCoeffs's own blockSize == size check
                // handles both correctly without this call site needing to special-case n == 1 itself.
                Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 0, subC, subR, s.YCoeffCtx, writeLumaTxType: null, blockSize: blockSize);
                Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, subX, subY, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);
                SetBlockDecoded(s, 0, subBlockMiRow, subBlockMiCol, true);
            }
        }
    }
}
