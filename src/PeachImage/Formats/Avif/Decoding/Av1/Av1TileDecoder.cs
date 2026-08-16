using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// Decodes one tile's partition tree and per-block mode info (spec §5.11.2-§5.11.25, restricted to the
/// intra-frame path since <c>FrameIsIntra</c> is always true for AVIF still images): the recursive
/// superblock partition walk (<c>decode_partition</c>), and for every leaf block,
/// <c>intra_frame_mode_info()</c> (segment id, skip, delta-q/lf, Y/UV intra mode with CFL, angle delta,
/// filter-intra, transform size). Stops at the boundary of <c>residual()</c>/<c>coeffs()</c> -- coefficient
/// decode and reconstruction are not implemented yet, so a leaf block's residual is where this throws.
/// </summary>
/// <remarks>
/// Neighbor context (<c>YModes</c>, <c>MiSizes</c>, <c>Skips</c>, <c>SegmentIds</c>) is stored in
/// frame-sized flat arrays shared across every tile of the frame, rather than being reset per tile. This
/// is safe without extra bookkeeping because every neighbor read in the spec is gated by
/// <c>is_inside()</c>, which never returns true for a position outside the current tile's own
/// [MiRowStart,MiRowEnd) x [MiColStart,MiColEnd) bounds -- so a later tile can never observe an earlier
/// tile's leftover values, even though the backing storage is shared.
/// </remarks>
internal sealed class Av1TileDecoder
{
    private readonly Av1SymbolDecoder _s;
    private readonly Av1CdfContext _cdf;
    private readonly Av1SequenceHeader _seq;
    private readonly Av1FrameHeader _frame;

    private readonly int _miRowStart;
    private readonly int _miRowEnd;
    private readonly int _miColStart;
    private readonly int _miColEnd;
    private readonly int _miRows;
    private readonly int _miCols;

    // Frame-sized neighbor context, flat [row * miCols + col].
    private readonly int[] _yModes;
    private readonly int[] _uvModes;
    private readonly int[] _miSizes;
    private readonly bool[] _skips;
    private readonly int[] _segmentIds;
    private readonly int[] _interTxSizes;

    // Frame-sized reconstructed pixel planes (spec's CurrFrame), shared across every tile of the frame --
    // safe for the same reason the neighbor-context arrays above are: every read this decoder performs
    // either targets already-reconstructed positions (raster decode order) or is gated by AvailU/AvailL
    // (which are themselves is_inside()-gated, so a tile never observes another tile's pixels through
    // those paths; direct CurrFrame prediction reads at tile edges are intentionally NOT gated the same
    // way -- matching the spec, which allows intra prediction to read reconstructed samples from a
    // different tile when available, only AvailU/AvailL/AvailAboveRight/AvailBelowLeft controls whether a
    // neighbor is treated as available at all).
    private readonly int[][] _planes;
    private readonly int[] _planeWidths;
    private readonly int[] _planeHeights;

    // Per-superblock BlockDecoded flags (spec §5.11.3), one flat array per plane sized for the largest
    // possible superblock (128x128 luma -> 32 4x4 units per side) with a uniform +1 offset so index -1 is
    // valid; only the [0, sbSize4>>sub] sub-range is ever meaningful for a given plane/superblock, per
    // clear_block_decoded_flags().
    private const int BlockDecodedStride = 34;
    private readonly bool[][] _blockDecoded = [new bool[BlockDecodedStride * BlockDecodedStride], new bool[BlockDecodedStride * BlockDecodedStride], new bool[BlockDecodedStride * BlockDecodedStride]];

    private int _maxLumaW;
    private int _maxLumaH;

    // Reusable scratch buffers for reconstruction (spec §7.12.3), sized for the largest possible
    // transform block (64x64) to avoid a heap allocation per transform block decoded.
    private readonly int[] _reconPred = new int[64 * 64];
    private readonly int[] _reconDequant = new int[64 * 64];
    private readonly int[] _reconResidual = new int[64 * 64];

    // Reusable AboveRow/LeftCol edge buffers for intra prediction (spec §7.11.2.1), sized for the
    // largest possible capacity (4*(w+h)+16 with w+h maxing out at 64+64) and refilled by
    // Av1IntraPrediction.BuildEdges for every transform block rather than allocated per call.
    private readonly Av1EdgeArray _aboveRow = new(528);
    private readonly Av1EdgeArray _leftCol = new(528);

    private int _currentQIndex;
    private readonly int[] _deltaLf = new int[4];
    private bool _readDeltas;

    // Per-block-position snapshots of DeltaLF (spec's frame-sized DeltaLFs[row][col][i]), consumed by the
    // deblocking filter's adaptive-strength selection. Frame-shared, one flat [row*miCols+col] array per
    // of the 4 FRAME_LF_COUNT slots.
    private readonly int[][] _deltaLfs;

    // Frame-shared CDEF index per 64x64 luma unit (spec's cdef_idx[r][c], sparse: only ever written at
    // 64x64-mi-aligned positions, -1 meaning "not yet assigned" per clear_cdef()). Flat [row*miCols+col].
    private readonly int[] _cdefIdx;

    // Frame-shared per-plane transform size used by the deblocking filter (spec's
    // LoopfilterTxSizes[plane][row>>subY][col>>subX]), indexed in that plane's own (possibly subsampled)
    // mi grid -- see _loopfilterTxSizeStrides for each plane's row stride.
    private readonly int[][] _loopfilterTxSizes;
    private readonly int[] _loopfilterTxSizeStrides;

    // Frame-shared per-plane loop restoration unit grids (spec's LrType/LrWiener/LrSgrSet/LrSgrXqd),
    // populated by read_lr_unit() and consumed by the loop restoration filter pass. Null entries mean
    // that plane's FrameRestorationType is RESTORE_NONE.
    private readonly Av1RestorationUnitGrid?[] _restorationUnits;

    // Per-tile state reset at the start of decode_tile() (spec §5.11.2): reference values used by the
    // subexp-with-reference decoding in read_lr_unit(), one set per plane per Wiener pass / SGRPROJ index.
    private readonly int[][] _refLrWiener = [new int[3], new int[3], new int[3], new int[3], new int[3], new int[3]]; // [plane*2+pass][coeff]
    private readonly int[][] _refSgrXqd = [new int[2], new int[2], new int[2]]; // [plane][i]

    private static readonly int[] WienerTapsMid = [3, -7, 15];
    private static readonly int[] SgrprojXqdMid = [-32, 31];

    // Per-block transient state (mirrors the syntax tables' block-scoped variables).
    private bool _availU;
    private bool _availL;
    private bool _availUChroma;
    private bool _availLChroma;
    private int _miRow;
    private int _miCol;
    private int _miSize;
    private bool _hasChroma;
    private bool _skip;
    private int _segmentId;
    private bool _lossless;
    private int _yMode;
    private int _uvMode;
    private int _angleDeltaY;
    private int _angleDeltaUv;
    private int _cflAlphaU;
    private int _cflAlphaV;
    private bool _useFilterIntra;
    private int _filterIntraMode;
    private int _txSize;

    // Coefficient decode scratch/context (spec §5.11.34/§5.11.39). AboveLevelContext/AboveDcContext span
    // the tile's full width (reset once per tile, per plane); LeftLevelContext/LeftDcContext are sized to
    // the whole frame height and re-cleared every superblock row (matching clear_left_context()) rather
    // than being windowed to one superblock row's height -- simpler and correctness-equivalent, at the
    // cost of clearing more than strictly necessary each row (a Phase 5 performance concern, not a
    // correctness one). Quant is reused across every transform block, sized to the largest possible
    // segEob (1024).
    private readonly int[][] _aboveLevelContext = [Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()];
    private readonly int[][] _aboveDcContext = [Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()];
    private readonly int[][] _leftLevelContext = [Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()];
    private readonly int[][] _leftDcContext = [Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()];
    private readonly int[] _quant = new int[1024];

    // Per-coeffs()-call transient state.
    private int _planeTxType;
    private int _eobValue;

    public int BlocksDecoded { get; private set; }

    /// <summary>
    /// The underlying symbol decoder's <c>SymbolMaxBits</c> once <see cref="DecodeTile"/> returns. Spec
    /// §8.2.4 (<c>exit_symbol()</c>) requires conformant bitstreams to leave this &gt;= -14 -- a strong,
    /// spec-mandated correctness check independent of any oracle: a bit-level desync anywhere in the
    /// partition tree, mode info, or coefficient decode would almost certainly have driven this far more
    /// negative (or thrown outright) long before the tile's real data was exhausted.
    /// </summary>
    public int SymbolMaxBitsAtExit => _s.SymbolMaxBits;

    /// <summary>
    /// Set once decode has run to completion. Named for historical continuity with the phase where this
    /// decoder genuinely stopped at the residual boundary; today it's purely informational; pixel
    /// prediction/reconstruction are still not implemented, but that no longer gates how much of the tile
    /// gets decoded -- neither consumes any entropy-coded bits (prediction is deterministic pixel math
    /// from already-known mode info, and reconstruction is deterministic math over already-decoded
    /// coefficients), so the entire tile's partition tree, mode info, and coefficient data can be fully
    /// entropy-decoded without them. <see cref="BlocksDecoded"/> reflects the true count across the whole
    /// tile as a result.
    /// </summary>
    public bool StoppedAtResidual { get; private set; }

    public Av1TileDecoder(
        Av1SymbolDecoder symbols,
        Av1CdfContext cdf,
        Av1SequenceHeader seq,
        Av1FrameHeader frame,
        int miRowStart,
        int miRowEnd,
        int miColStart,
        int miColEnd,
        int[] yModes,
        int[] uvModes,
        int[] miSizes,
        bool[] skips,
        int[] segmentIds,
        int[] interTxSizes,
        int[][] planes,
        int[] planeWidths,
        int[] planeHeights,
        int[][] deltaLfs,
        int[] cdefIdx,
        int[][] loopfilterTxSizes,
        int[] loopfilterTxSizeStrides,
        Av1RestorationUnitGrid?[] restorationUnits)
    {
        _s = symbols;
        _cdf = cdf;
        _seq = seq;
        _frame = frame;
        _miRowStart = miRowStart;
        _miRowEnd = miRowEnd;
        _miColStart = miColStart;
        _miColEnd = miColEnd;
        _miRows = frame.MiRows;
        _miCols = frame.MiCols;
        _yModes = yModes;
        _uvModes = uvModes;
        _miSizes = miSizes;
        _skips = skips;
        _segmentIds = segmentIds;
        _interTxSizes = interTxSizes;
        _planes = planes;
        _planeWidths = planeWidths;
        _planeHeights = planeHeights;
        _deltaLfs = deltaLfs;
        _cdefIdx = cdefIdx;
        _loopfilterTxSizes = loopfilterTxSizes;
        _loopfilterTxSizeStrides = loopfilterTxSizeStrides;
        _restorationUnits = restorationUnits;

        for (int plane = 0; plane < 3; plane++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                WienerTapsMid.CopyTo(_refLrWiener[(plane * 2) + pass], 0);
            }

            SgrprojXqdMid.CopyTo(_refSgrXqd[plane], 0);
        }
    }

    /// <summary><c>decode_tile()</c> (spec §5.11.2).</summary>
    public void DecodeTile()
    {
        _currentQIndex = _frame.BaseQIdx;
        Array.Clear(_deltaLf);
        ClearAboveContext();

        int sbSize = _seq.Use128x128Superblock ? Av1BlockSize.Block128x128 : Av1BlockSize.Block64x64;
        int sbSize4 = Av1BlockTables.Num4x4BlocksWide[sbSize];

        for (int r = _miRowStart; r < _miRowEnd; r += sbSize4)
        {
            ClearLeftContext();

            for (int c = _miColStart; c < _miColEnd; c += sbSize4)
            {
                _readDeltas = _frame.DeltaQPresent;
                ClearCdef(r, c);
                ClearBlockDecodedFlags(r, c, sbSize4);
                ReadLr(r, c, sbSize);
                DecodePartition(r, c, sbSize);
            }
        }

        StoppedAtResidual = true;
    }

    /// <summary><c>clear_cdef(r, c)</c> (spec §5.11.55).</summary>
    private void ClearCdef(int r, int c)
    {
        SetCdefIdx(r, c, -1);
        if (_seq.Use128x128Superblock)
        {
            int cdefSize4 = Av1BlockTables.Num4x4BlocksWide[Av1BlockSize.Block64x64];
            SetCdefIdx(r, c + cdefSize4, -1);
            SetCdefIdx(r + cdefSize4, c, -1);
            SetCdefIdx(r + cdefSize4, c + cdefSize4, -1);
        }
    }

    private void SetCdefIdx(int r, int c, int value)
    {
        if (r < _miRows && c < _miCols)
        {
            _cdefIdx[(r * _miCols) + c] = value;
        }
    }

    /// <summary><c>read_cdef()</c> (spec §5.11.56).</summary>
    private void ReadCdef()
    {
        if (_skip || _frame.CodedLossless || !_seq.EnableCdef)
        {
            return;
        }

        int cdefSize4 = Av1BlockTables.Num4x4BlocksWide[Av1BlockSize.Block64x64];
        int cdefMask4 = ~(cdefSize4 - 1);
        int r = _miRow & cdefMask4;
        int c = _miCol & cdefMask4;

        if (_cdefIdx[(r * _miCols) + c] == -1)
        {
            int idx = (int)_s.ReadLiteral(_frame.Cdef.Bits);
            int w4 = Av1BlockTables.Num4x4BlocksWide[_miSize];
            int h4 = Av1BlockTables.Num4x4BlocksHigh[_miSize];

            for (int i = r; i < r + h4; i += cdefSize4)
            {
                for (int j = c; j < c + w4; j += cdefSize4)
                {
                    SetCdefIdx(i, j, idx);
                }
            }
        }
    }

    /// <summary><c>read_lr(r, c, bSize)</c> (spec §5.11.57), restricted to the non-superres path (superres is rejected at frame-header parse time, so <c>use_superres</c> is always false).</summary>
    private void ReadLr(int r, int c, int bSize)
    {
        int w = Av1BlockTables.Num4x4BlocksWide[bSize];
        int h = Av1BlockTables.Num4x4BlocksHigh[bSize];

        for (int plane = 0; plane < _seq.NumPlanes; plane++)
        {
            var grid = _restorationUnits[plane];
            if (grid is null)
            {
                continue;
            }

            int subX = plane == 0 ? 0 : _seq.SubsamplingX ? 1 : 0;
            int subY = plane == 0 ? 0 : _seq.SubsamplingY ? 1 : 0;
            int unitSize = grid.UnitSize;

            int unitRowStart = (((r * 4) >> subY) + unitSize - 1) / unitSize;
            int unitRowEnd = Math.Min(grid.UnitRows, ((((r + h) * 4) >> subY) + unitSize - 1) / unitSize);

            int numerator = 4 >> subX;
            int denominator = unitSize;

            int unitColStart = ((c * numerator) + denominator - 1) / denominator;
            int unitColEnd = Math.Min(grid.UnitCols, (((c + w) * numerator) + denominator - 1) / denominator);

            for (int unitRow = unitRowStart; unitRow < unitRowEnd; unitRow++)
            {
                for (int unitCol = unitColStart; unitCol < unitColEnd; unitCol++)
                {
                    ReadLrUnit(plane, grid, unitRow, unitCol);
                }
            }
        }
    }

    /// <summary><c>read_lr_unit(plane, unitRow, unitCol)</c> (spec §5.11.58).</summary>
    private void ReadLrUnit(int plane, Av1RestorationUnitGrid grid, int unitRow, int unitCol)
    {
        int unitIdx = (unitRow * grid.UnitCols) + unitCol;
        int frameRestorationType = _frame.LoopRestoration.FrameRestorationType[plane];

        int restorationType;
        if (frameRestorationType == Av1LoopRestorationParams.RestoreWiener)
        {
            bool useWiener = _s.ReadSymbol(_cdf.UseWiener) != 0;
            restorationType = useWiener ? Av1LoopRestorationParams.RestoreWiener : Av1LoopRestorationParams.RestoreNone;
        }
        else if (frameRestorationType == Av1LoopRestorationParams.RestoreSgrproj)
        {
            bool useSgrproj = _s.ReadSymbol(_cdf.UseSgrproj) != 0;
            restorationType = useSgrproj ? Av1LoopRestorationParams.RestoreSgrproj : Av1LoopRestorationParams.RestoreNone;
        }
        else
        {
            restorationType = _s.ReadSymbol(_cdf.RestorationType);
        }

        grid.LrType[unitIdx] = restorationType;

        if (restorationType == Av1LoopRestorationParams.RestoreWiener)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var refCoeffs = _refLrWiener[(plane * 2) + pass];
                int firstCoeff = plane != 0 ? 1 : 0;
                if (plane != 0)
                {
                    grid.LrWiener[(((unitIdx * 2) + pass) * 3) + 0] = 0;
                }

                for (int j = firstCoeff; j < 3; j++)
                {
                    int min = WienerTapsMin[j];
                    int max = WienerTapsMax[j];
                    int k = WienerTapsK[j];
                    int v = DecodeSignedSubexpWithRefBool(min, max + 1, k, refCoeffs[j]);
                    grid.LrWiener[(((unitIdx * 2) + pass) * 3) + j] = v;
                    refCoeffs[j] = v;
                }
            }
        }
        else if (restorationType == Av1LoopRestorationParams.RestoreSgrproj)
        {
            int lrSgrSet = (int)_s.ReadLiteral(SgrprojParamsBits);
            grid.LrSgrSet[unitIdx] = lrSgrSet;

            for (int i = 0; i < 2; i++)
            {
                int radius = Av1SgrParams.Table[lrSgrSet][i * 2];
                int min = SgrprojXqdMin[i];
                int max = SgrprojXqdMax[i];
                int v;
                if (radius != 0)
                {
                    v = DecodeSignedSubexpWithRefBool(min, max + 1, SgrprojPrjSubexpK, _refSgrXqd[plane][i]);
                }
                else
                {
                    v = 0;
                    if (i == 1)
                    {
                        v = Math.Clamp((1 << SgrprojPrjBits) - _refSgrXqd[plane][0], min, max);
                    }
                }

                grid.LrSgrXqd[(unitIdx * 2) + i] = v;
                _refSgrXqd[plane][i] = v;
            }
        }
    }

    private const int SgrprojParamsBits = 4;
    private const int SgrprojPrjSubexpK = 4;
    private const int SgrprojPrjBits = 7;

    private static readonly int[] WienerTapsMin = [-5, -23, -17];
    private static readonly int[] WienerTapsMax = [10, 8, 46];
    private static readonly int[] WienerTapsK = [1, 2, 3];
    private static readonly int[] SgrprojXqdMin = [-96, -32];
    private static readonly int[] SgrprojXqdMax = [31, 95];

    /// <summary><c>decode_signed_subexp_with_ref_bool(low, high, k, r)</c> (spec §5.11.58).</summary>
    private int DecodeSignedSubexpWithRefBool(int low, int high, int k, int r) =>
        DecodeUnsignedSubexpWithRefBool(high - low, k, r - low) + low;

    /// <summary><c>decode_unsigned_subexp_with_ref_bool(mx, k, r)</c> (spec §5.11.58).</summary>
    private int DecodeUnsignedSubexpWithRefBool(int mx, int k, int r)
    {
        int v = DecodeSubexpBool(mx, k);
        if ((r << 1) <= mx)
        {
            return InverseRecenter(r, v);
        }

        return mx - 1 - InverseRecenter(mx - 1 - r, v);
    }

    /// <summary><c>decode_subexp_bool(numSyms, k)</c> (spec §5.11.58).</summary>
    private int DecodeSubexpBool(int numSyms, int k)
    {
        int i = 0;
        int mk = 0;
        while (true)
        {
            int b2 = i != 0 ? k + i - 1 : k;
            int a = 1 << b2;
            if (numSyms <= mk + (3 * a))
            {
                int subexpUnifBools = ReadNs(numSyms - mk);
                return subexpUnifBools + mk;
            }

            bool subexpMoreBools = _s.ReadLiteral(1) != 0;
            if (subexpMoreBools)
            {
                i++;
                mk += a;
            }
            else
            {
                int subexpBools = (int)_s.ReadLiteral(b2);
                return subexpBools + mk;
            }
        }
    }

    /// <summary><c>NS(n)</c> (spec §4.10.7), reading via the arithmetic-coded literal primitives since this is invoked from within tile data.</summary>
    private int ReadNs(int n)
    {
        int w = FloorLog2(n) + 1;
        int m = (1 << w) - n;
        int v = (int)_s.ReadLiteral(w - 1);
        if (v < m)
        {
            return v;
        }

        int extraBit = (int)_s.ReadLiteral(1);
        return (v << 1) - m + extraBit;
    }

    private static int FloorLog2(int x)
    {
        int s = 0;
        while (x != 0)
        {
            x >>= 1;
            s++;
        }

        return s - 1;
    }

    /// <summary><c>inverse_recenter(r, v)</c> (spec §5.9.29).</summary>
    private static int InverseRecenter(int r, int v)
    {
        if (v > 2 * r)
        {
            return v;
        }

        if ((v & 1) != 0)
        {
            return r - ((v + 1) >> 1);
        }

        return r + (v >> 1);
    }

    private void ClearAboveContext()
    {
        for (int plane = 0; plane < 3; plane++)
        {
            _aboveLevelContext[plane] = new int[_miCols + 32];
            _aboveDcContext[plane] = new int[_miCols + 32];
        }
    }

    private void ClearLeftContext()
    {
        for (int plane = 0; plane < 3; plane++)
        {
            _leftLevelContext[plane] = new int[_miRows + 32];
            _leftDcContext[plane] = new int[_miRows + 32];
        }
    }

    /// <summary><c>clear_block_decoded_flags(r, c, sbSize4)</c> (spec §5.11.3).</summary>
    private void ClearBlockDecodedFlags(int r, int c, int sbSize4)
    {
        for (int plane = 0; plane < _seq.NumPlanes; plane++)
        {
            int subX = plane > 0 && _seq.SubsamplingX ? 1 : 0;
            int subY = plane > 0 && _seq.SubsamplingY ? 1 : 0;
            int sbWidth4 = (_miColEnd - c) >> subX;
            int sbHeight4 = (_miRowEnd - r) >> subY;

            for (int y = -1; y <= sbSize4 >> subY; y++)
            {
                for (int x = -1; x <= sbSize4 >> subX; x++)
                {
                    bool value = (y < 0 && x < sbWidth4) || (x < 0 && y < sbHeight4);
                    SetBlockDecoded(plane, y, x, value);
                }
            }

            SetBlockDecoded(plane, sbSize4 >> subY, -1, false);
        }
    }

    private bool GetBlockDecoded(int plane, int y, int x) => _blockDecoded[plane][((y + 1) * BlockDecodedStride) + x + 1];

    private void SetBlockDecoded(int plane, int y, int x, bool value) => _blockDecoded[plane][((y + 1) * BlockDecodedStride) + x + 1] = value;

    /// <summary><c>decode_partition(r, c, bSize)</c> (spec §5.11.4).</summary>
    private void DecodePartition(int r, int c, int bSize)
    {
        if (r >= _miRows || c >= _miCols)
        {
            return;
        }

        _availU = IsInside(r - 1, c);
        _availL = IsInside(r, c - 1);
        int num4x4 = Av1BlockTables.Num4x4BlocksWide[bSize];
        int halfBlock4x4 = num4x4 >> 1;
        int quarterBlock4x4 = halfBlock4x4 >> 1;
        bool hasRows = r + halfBlock4x4 < _miRows;
        bool hasCols = c + halfBlock4x4 < _miCols;

        int partition;
        if (bSize < Av1BlockSize.Block8x8)
        {
            partition = Av1PartitionType.None;
        }
        else if (hasRows && hasCols)
        {
            partition = ReadPartitionSymbol(r, c, bSize);
        }
        else if (hasCols)
        {
            partition = ReadSplitOrHorz(r, c, bSize) ? Av1PartitionType.Split : Av1PartitionType.Horz;
        }
        else if (hasRows)
        {
            partition = ReadSplitOrVert(r, c, bSize) ? Av1PartitionType.Split : Av1PartitionType.Vert;
        }
        else
        {
            partition = Av1PartitionType.Split;
        }

        int subSize = Av1BlockTables.PartitionSubsize[partition][bSize];
        int splitSize = Av1BlockTables.PartitionSubsize[Av1PartitionType.Split][bSize];

        switch (partition)
        {
            case Av1PartitionType.None:
                DecodeBlock(r, c, subSize);
                break;

            case Av1PartitionType.Horz:
                DecodeBlock(r, c, subSize);
                if (hasRows)
                {
                    DecodeBlock(r + halfBlock4x4, c, subSize);
                }

                break;

            case Av1PartitionType.Vert:
                DecodeBlock(r, c, subSize);
                if (hasCols)
                {
                    DecodeBlock(r, c + halfBlock4x4, subSize);
                }

                break;

            case Av1PartitionType.Split:
                DecodePartition(r, c, subSize);
                DecodePartition(r, c + halfBlock4x4, subSize);
                DecodePartition(r + halfBlock4x4, c, subSize);
                DecodePartition(r + halfBlock4x4, c + halfBlock4x4, subSize);
                break;

            case Av1PartitionType.HorzA:
                DecodeBlock(r, c, splitSize);
                DecodeBlock(r, c + halfBlock4x4, splitSize);
                DecodeBlock(r + halfBlock4x4, c, subSize);
                break;

            case Av1PartitionType.HorzB:
                DecodeBlock(r, c, subSize);
                DecodeBlock(r + halfBlock4x4, c, splitSize);
                DecodeBlock(r + halfBlock4x4, c + halfBlock4x4, splitSize);
                break;

            case Av1PartitionType.VertA:
                DecodeBlock(r, c, splitSize);
                DecodeBlock(r + halfBlock4x4, c, splitSize);
                DecodeBlock(r, c + halfBlock4x4, subSize);
                break;

            case Av1PartitionType.VertB:
                DecodeBlock(r, c, subSize);
                DecodeBlock(r, c + halfBlock4x4, splitSize);
                DecodeBlock(r + halfBlock4x4, c + halfBlock4x4, splitSize);
                break;

            case Av1PartitionType.Horz4:
                DecodeBlock(r + (quarterBlock4x4 * 0), c, subSize);
                DecodeBlock(r + (quarterBlock4x4 * 1), c, subSize);
                DecodeBlock(r + (quarterBlock4x4 * 2), c, subSize);
                if (r + (quarterBlock4x4 * 3) < _miRows)
                {
                    DecodeBlock(r + (quarterBlock4x4 * 3), c, subSize);
                }

                break;

            default: // PARTITION_VERT_4
                DecodeBlock(r, c + (quarterBlock4x4 * 0), subSize);
                DecodeBlock(r, c + (quarterBlock4x4 * 1), subSize);
                DecodeBlock(r, c + (quarterBlock4x4 * 2), subSize);
                if (c + (quarterBlock4x4 * 3) < _miCols)
                {
                    DecodeBlock(r, c + (quarterBlock4x4 * 3), subSize);
                }

                break;
        }
    }

    /// <summary><c>decode_block(r, c, subSize)</c> (spec §5.11.5). Prediction/reconstruction (the pixel-producing parts of <c>transform_block()</c>, called from <see cref="Residual"/>) are skipped -- see <see cref="StoppedAtResidual"/>'s remarks for why that's safe for entropy-decode purposes.</summary>
    private void DecodeBlock(int r, int c, int subSize)
    {
        _miRow = r;
        _miCol = c;
        _miSize = subSize;

        int bw4 = Av1BlockTables.Num4x4BlocksWide[subSize];
        int bh4 = Av1BlockTables.Num4x4BlocksHigh[subSize];

        if (bh4 == 1 && _seq.SubsamplingY && (r & 1) == 0)
        {
            _hasChroma = false;
        }
        else if (bw4 == 1 && _seq.SubsamplingX && (c & 1) == 0)
        {
            _hasChroma = false;
        }
        else
        {
            _hasChroma = _seq.NumPlanes > 1;
        }

        _availU = IsInside(r - 1, c);
        _availL = IsInside(r, c - 1);

        // AvailUChroma/AvailLChroma (spec §5.11.5): for a 1-mi-tall/wide block under subsampling, the
        // block shares its chroma with the sibling above/left, so chroma availability must be checked two
        // mi units away rather than one.
        _availUChroma = _availU;
        _availLChroma = _availL;
        if (_hasChroma)
        {
            if (_seq.SubsamplingY && bh4 == 1)
            {
                _availUChroma = IsInside(r - 2, c);
            }

            if (_seq.SubsamplingX && bw4 == 1)
            {
                _availLChroma = IsInside(r, c - 2);
            }
        }
        else
        {
            _availUChroma = false;
            _availLChroma = false;
        }

        IntraFrameModeInfo();

        ReadBlockTxSize(bw4, bh4);

        for (int y = 0; y < bh4 && r + y < _miRows; y++)
        {
            for (int x = 0; x < bw4 && c + x < _miCols; x++)
            {
                int idx = ((r + y) * _miCols) + c + x;
                _yModes[idx] = _yMode;
                _uvModes[idx] = _uvMode;
                _miSizes[idx] = subSize;
                _skips[idx] = _skip;
                _segmentIds[idx] = _segmentId;
                for (int i = 0; i < 4; i++)
                {
                    _deltaLfs[i][idx] = _deltaLf[i];
                }
            }
        }

        Residual(bw4, bh4);

        BlocksDecoded++;
    }

    /// <summary><c>intra_frame_mode_info()</c> (spec §5.11.7), restricted to the non-IntraBC path (IntraBC is rejected earlier, at frame-header parse time).</summary>
    private void IntraFrameModeInfo()
    {
        _skip = false;

        if (_frame.Segmentation.SegIdPreSkip)
        {
            IntraSegmentId();
        }

        ReadSkip();

        if (!_frame.Segmentation.SegIdPreSkip)
        {
            IntraSegmentId();
        }

        ReadCdef();
        ReadDeltaQIndex();
        ReadDeltaLf();
        _readDeltas = false;

        // allow_intrabc is always false here (rejected during frame-header parsing), so use_intrabc is
        // never read and is unconditionally 0 -- go straight to the intra-mode path.
        _angleDeltaY = 0;
        _angleDeltaUv = 0;
        _cflAlphaU = 0;
        _cflAlphaV = 0;

        int aboveMode = Av1BlockTables.IntraModeContext[_availU ? _yModes[((_miRow - 1) * _miCols) + _miCol] : Av1IntraMode.DcPred];
        int leftMode = Av1BlockTables.IntraModeContext[_availL ? _yModes[(_miRow * _miCols) + _miCol - 1] : Av1IntraMode.DcPred];
        _yMode = _s.ReadSymbol(_cdf.IntraFrameYMode[aboveMode][leftMode]);

        IntraAngleInfoY();

        if (_hasChroma)
        {
            _uvMode = ReadUvMode();
            if (_uvMode == Av1IntraMode.UvCflPred)
            {
                ReadCflAlphas();
            }

            IntraAngleInfoUv();
        }
        else
        {
            _uvMode = Av1IntraMode.DcPred;
        }

        // PaletteSizeY/PaletteSizeUV are always 0 (palette mode is rejected during frame-header parsing
        // via allow_screen_content_tools, since palette_mode_info() is only ever invoked when it's set).
        FilterIntraModeInfo();
    }

    /// <summary><c>intra_segment_id()</c> (spec §5.11.8).</summary>
    private void IntraSegmentId()
    {
        if (_frame.Segmentation.Enabled)
        {
            ReadSegmentId();
        }
        else
        {
            _segmentId = 0;
        }

        _lossless = IsSegmentLossless(_segmentId);
    }

    private bool IsSegmentLossless(int segmentId)
    {
        int qindex = GetQIndexIgnoringDeltaQ(segmentId);
        return qindex == 0 && _frame.DeltaQYDc == 0 && _frame.DeltaQUAc == 0 && _frame.DeltaQUDc == 0 && _frame.DeltaQVAc == 0 && _frame.DeltaQVDc == 0;
    }

    private int GetQIndexIgnoringDeltaQ(int segmentId)
    {
        if (_frame.Segmentation.Enabled && _frame.Segmentation.FeatureEnabled[segmentId, 0])
        {
            int data = _frame.Segmentation.FeatureData[segmentId, 0];
            return Math.Clamp(_frame.BaseQIdx + data, 0, 255);
        }

        return _frame.BaseQIdx;
    }

    /// <summary><c>read_segment_id()</c> (spec §5.11.9), including <c>neg_deinterleave</c>.</summary>
    private void ReadSegmentId()
    {
        int prevUL = _availU && _availL ? _segmentIds[((_miRow - 1) * _miCols) + _miCol - 1] : -1;
        int prevU = _availU ? _segmentIds[((_miRow - 1) * _miCols) + _miCol] : -1;
        int prevL = _availL ? _segmentIds[(_miRow * _miCols) + _miCol - 1] : -1;

        int pred;
        if (prevU == -1)
        {
            pred = prevL == -1 ? 0 : prevL;
        }
        else if (prevL == -1)
        {
            pred = prevU;
        }
        else
        {
            pred = prevUL == prevU ? prevU : prevL;
        }

        if (_skip)
        {
            _segmentId = pred;
            return;
        }

        int ctx;
        if (prevUL < 0)
        {
            ctx = 0;
        }
        else if (prevUL == prevU && prevUL == prevL)
        {
            ctx = 2;
        }
        else if (prevUL == prevU || prevUL == prevL || prevU == prevL)
        {
            ctx = 1;
        }
        else
        {
            ctx = 0;
        }

        int segmentIdSymbol = _s.ReadSymbol(_cdf.SegmentId[ctx]);
        _segmentId = NegDeinterleave(segmentIdSymbol, pred, _frame.Segmentation.LastActiveSegId + 1);
    }

    private static int NegDeinterleave(int diff, int reference, int max)
    {
        if (reference == 0)
        {
            return diff;
        }

        if (reference >= max - 1)
        {
            return max - diff - 1;
        }

        if (2 * reference < max)
        {
            if (diff <= 2 * reference)
            {
                return (diff & 1) != 0 ? reference + ((diff + 1) >> 1) : reference - (diff >> 1);
            }

            return diff;
        }

        if (diff <= 2 * (max - reference - 1))
        {
            return (diff & 1) != 0 ? reference + ((diff + 1) >> 1) : reference - (diff >> 1);
        }

        return max - (diff + 1);
    }

    /// <summary><c>read_skip()</c> (spec §5.11.11).</summary>
    private void ReadSkip()
    {
        if (_frame.Segmentation.SegIdPreSkip && SegFeatureActive(_segmentId, SegLvlSkip))
        {
            _skip = true;
            return;
        }

        int ctx = 0;
        if (_availU)
        {
            ctx += _skips[((_miRow - 1) * _miCols) + _miCol] ? 1 : 0;
        }

        if (_availL)
        {
            ctx += _skips[(_miRow * _miCols) + _miCol - 1] ? 1 : 0;
        }

        _skip = _s.ReadSymbol(_cdf.Skip[ctx]) != 0;
    }

    private const int SegLvlSkip = 6;

    private bool SegFeatureActive(int segmentId, int feature) =>
        _frame.Segmentation.Enabled && _frame.Segmentation.FeatureEnabled[segmentId, feature];

    /// <summary><c>read_delta_qindex()</c> (spec §5.11.12).</summary>
    private void ReadDeltaQIndex()
    {
        int sbSize = _seq.Use128x128Superblock ? Av1BlockSize.Block128x128 : Av1BlockSize.Block64x64;
        if (_miSize == sbSize && _skip)
        {
            return;
        }

        if (!_readDeltas)
        {
            return;
        }

        const int deltaQSmall = 3;
        int deltaQAbs = _s.ReadSymbol(_cdf.DeltaQ);
        if (deltaQAbs == deltaQSmall)
        {
            int deltaQRemBits = (int)_s.ReadLiteral(3) + 1;
            int deltaQAbsBits = (int)_s.ReadLiteral(deltaQRemBits);
            deltaQAbs = deltaQAbsBits + (1 << deltaQRemBits) + 1;
        }

        if (deltaQAbs != 0)
        {
            bool deltaQSignBit = _s.ReadLiteral(1) != 0;
            int reducedDeltaQIndex = deltaQSignBit ? -deltaQAbs : deltaQAbs;
            _currentQIndex = Math.Clamp(_currentQIndex + (reducedDeltaQIndex << _frame.DeltaQRes), 1, 255);
        }
    }

    /// <summary><c>read_delta_lf()</c> (spec §5.11.13).</summary>
    private void ReadDeltaLf()
    {
        int sbSize = _seq.Use128x128Superblock ? Av1BlockSize.Block128x128 : Av1BlockSize.Block64x64;
        if (_miSize == sbSize && _skip)
        {
            return;
        }

        if (!_readDeltas || !_frame.DeltaLfPresent)
        {
            return;
        }

        const int deltaLfSmall = 3;
        const int maxLoopFilter = 63;
        int frameLfCount = 1;
        if (_frame.DeltaLfMulti)
        {
            frameLfCount = _seq.NumPlanes > 1 ? 4 : 2;
        }

        for (int i = 0; i < frameLfCount; i++)
        {
            var cdf = _frame.DeltaLfMulti ? _cdf.DeltaLfMulti[i] : _cdf.DeltaLf;
            int deltaLfAbs = _s.ReadSymbol(cdf);
            if (deltaLfAbs == deltaLfSmall)
            {
                int deltaLfRemBits = (int)_s.ReadLiteral(3);
                int n = deltaLfRemBits + 1;
                int deltaLfAbsBits = (int)_s.ReadLiteral(n);
                deltaLfAbs = deltaLfAbsBits + (1 << n) + 1;
            }

            if (deltaLfAbs != 0)
            {
                bool deltaLfSignBit = _s.ReadLiteral(1) != 0;
                int reducedDeltaLfLevel = deltaLfSignBit ? -deltaLfAbs : deltaLfAbs;
                _deltaLf[i] = Math.Clamp(_deltaLf[i] + (reducedDeltaLfLevel << _frame.DeltaLfRes), -maxLoopFilter, maxLoopFilter);
            }
        }
    }

    /// <summary><c>intra_angle_info_y()</c> (spec §5.11.42).</summary>
    private void IntraAngleInfoY()
    {
        _angleDeltaY = 0;
        if (_miSize >= Av1BlockSize.Block8x8 && Av1IntraMode.IsDirectional(_yMode))
        {
            const int maxAngleDelta = 3;
            int angleDeltaY = _s.ReadSymbol(_cdf.AngleDelta[_yMode - Av1IntraMode.VPred]);
            _angleDeltaY = angleDeltaY - maxAngleDelta;
        }
    }

    /// <summary><c>intra_angle_info_uv()</c> (spec §5.11.43).</summary>
    private void IntraAngleInfoUv()
    {
        _angleDeltaUv = 0;
        if (_miSize >= Av1BlockSize.Block8x8 && Av1IntraMode.IsDirectional(_uvMode))
        {
            const int maxAngleDelta = 3;
            int angleDeltaUv = _s.ReadSymbol(_cdf.AngleDelta[_uvMode - Av1IntraMode.VPred]);
            _angleDeltaUv = angleDeltaUv - maxAngleDelta;
        }
    }

    /// <summary><c>uv_mode</c>'s CDF selection (spec §8.3.2): CFL-allowed when lossless at a 4x4 chroma residual, or (non-lossless) the block's largest side is &lt;= 32.</summary>
    private int ReadUvMode()
    {
        bool cflAllowed;
        if (_lossless)
        {
            int chromaSize = Av1BlockTables.GetPlaneResidualSize(_miSize, 1, _seq.SubsamplingX, _seq.SubsamplingY);
            cflAllowed = chromaSize == Av1BlockSize.Block4x4;
        }
        else
        {
            cflAllowed = Math.Max(Av1BlockTables.BlockWidth(_miSize), Av1BlockTables.BlockHeight(_miSize)) <= 32;
        }

        var cdf = cflAllowed ? _cdf.UvModeCflAllowed[_yMode] : _cdf.UvModeCflNotAllowed[_yMode];
        return _s.ReadSymbol(cdf);
    }

    /// <summary><c>read_cfl_alphas()</c> (spec §5.11.45).</summary>
    private void ReadCflAlphas()
    {
        const int cflSignZero = 0;
        const int cflSignNeg = 1;

        int cflAlphaSigns = _s.ReadSymbol(_cdf.CflSign);
        int signU = (cflAlphaSigns + 1) / 3;
        int signV = (cflAlphaSigns + 1) % 3;

        if (signU != cflSignZero)
        {
            int ctx = ((signU - 1) * 3) + signV;
            int cflAlphaU = _s.ReadSymbol(_cdf.CflAlpha[ctx]);
            _cflAlphaU = signU == cflSignNeg ? -(1 + cflAlphaU) : 1 + cflAlphaU;
        }
        else
        {
            _cflAlphaU = 0;
        }

        if (signV != cflSignZero)
        {
            int ctx = ((signV - 1) * 3) + signU;
            int cflAlphaV = _s.ReadSymbol(_cdf.CflAlpha[ctx]);
            _cflAlphaV = signV == cflSignNeg ? -(1 + cflAlphaV) : 1 + cflAlphaV;
        }
        else
        {
            _cflAlphaV = 0;
        }
    }

    /// <summary><c>filter_intra_mode_info()</c> (spec §5.11.24).</summary>
    private void FilterIntraModeInfo()
    {
        _useFilterIntra = false;
        _filterIntraMode = 0;

        if (_seq.EnableFilterIntra
            && _yMode == Av1IntraMode.DcPred
            && Math.Max(Av1BlockTables.BlockWidth(_miSize), Av1BlockTables.BlockHeight(_miSize)) <= 32)
        {
            _useFilterIntra = _s.ReadSymbol(_cdf.FilterIntra[_miSize]) != 0;
            if (_useFilterIntra)
            {
                _filterIntraMode = _s.ReadSymbol(_cdf.FilterIntraMode);
            }
        }
    }

    /// <summary><c>read_tx_size(allowSelect)</c> (spec §5.11.15).</summary>
    private void ReadTxSize(bool allowSelect)
    {
        if (_lossless)
        {
            _txSize = Av1TxSize.Tx4x4;
            return;
        }

        int maxRectTxSize = Av1BlockTables.MaxTxSizeRect[_miSize];
        _txSize = maxRectTxSize;

        if (_miSize > Av1BlockSize.Block4x4 && allowSelect && _frame.TxMode == Av1FrameHeader.TxModeSelect)
        {
            int txDepth = ReadTxDepth(maxRectTxSize);
            for (int i = 0; i < txDepth; i++)
            {
                _txSize = Av1BlockTables.SplitTxSize[_txSize];
            }
        }
    }

    /// <summary><c>tx_depth</c>'s CDF selection (spec §8.3.2).</summary>
    private int ReadTxDepth(int maxRectTxSize)
    {
        int maxTxWidth = Av1TxDimensions.Width[maxRectTxSize];
        int maxTxHeight = Av1TxDimensions.Height[maxRectTxSize];

        int aboveW;
        if (_availU && IsAboveInter)
        {
            aboveW = Av1BlockTables.BlockWidth(_miSizes[((_miRow - 1) * _miCols) + _miCol]);
        }
        else if (_availU)
        {
            aboveW = GetAboveTxWidth();
        }
        else
        {
            aboveW = 0;
        }

        int leftH;
        if (_availL && IsLeftInter)
        {
            leftH = Av1BlockTables.BlockHeight(_miSizes[(_miRow * _miCols) + _miCol - 1]);
        }
        else if (_availL)
        {
            leftH = GetLeftTxHeight();
        }
        else
        {
            leftH = 0;
        }

        int ctx = (aboveW >= maxTxWidth ? 1 : 0) + (leftH >= maxTxHeight ? 1 : 0);
        int maxTxDepth = Av1MaxTxDepth.Values[_miSize];

        var cdf = maxTxDepth switch
        {
            4 => _cdf.Tx64x64[ctx],
            3 => _cdf.Tx32x32[ctx],
            2 => _cdf.Tx16x16[ctx],
            _ => _cdf.Tx8x8[ctx],
        };

        return _s.ReadSymbol(cdf);
    }

    // This decoder is intra-only, so every already-decoded neighbor is intra (IsInters is never true);
    // kept as named constants matching the spec's own IsInters[] checks for readability/traceability
    // rather than inlining `false` at each call site.
    private const bool IsAboveInter = false;

    private const bool IsLeftInter = false;

    /// <summary>
    /// <c>get_above_tx_width(row, col)</c> (spec §8.3.2, defined alongside <c>txfm_split</c>), specialized
    /// to its only call site here (<c>tx_depth</c>'s context derivation, always called as
    /// <c>get_above_tx_width(MiRow, MiCol)</c> with <c>AvailU</c> already confirmed true by the caller) --
    /// the general form's <c>row == MiRow</c> and <c>!AvailU</c> branches are therefore always/never taken
    /// respectively and are omitted. <c>Skips[...] &amp;&amp; IsInters[...]</c> is always false (this
    /// decoder is intra-only), so this always falls through to the general
    /// <c>Tx_Width[InterTxSizes[row-1][col]]</c> case -- <see cref="_interTxSizes"/> is populated for
    /// every block (including intra ones) by <see cref="ReadBlockTxSize"/>, matching
    /// <c>read_block_tx_size()</c>'s own unconditional <c>InterTxSizes[row][col] = TxSize</c> write in its
    /// non-inter-var-tx branch.
    /// </summary>
    private int GetAboveTxWidth() => Av1TxDimensions.Width[_interTxSizes[((_miRow - 1) * _miCols) + _miCol]];

    /// <summary>Mirror of <see cref="GetAboveTxWidth"/> for the left neighbor.</summary>
    private int GetLeftTxHeight() => Av1TxDimensions.Height[_interTxSizes[(_miRow * _miCols) + _miCol - 1]];

    /// <summary>
    /// <c>read_block_tx_size()</c> (spec §5.11.16), restricted to the intra path -- the <c>is_inter</c>
    /// branch (which recurses via <c>read_var_tx_size</c>) is never taken here, so this always reduces to
    /// <c>read_tx_size(!skip || !is_inter)</c> (with <c>is_inter</c> always false, i.e. always
    /// <c>allowSelect = true</c>) followed by writing <see cref="_txSize"/> into every 4x4 position the
    /// block covers, exactly as the spec's non-inter-var-tx branch does.
    /// </summary>
    private void ReadBlockTxSize(int bw4, int bh4)
    {
        ReadTxSize(allowSelect: true);

        for (int row = _miRow; row < _miRow + bh4 && row < _miRows; row++)
        {
            for (int col = _miCol; col < _miCol + bw4 && col < _miCols; col++)
            {
                _interTxSizes[(row * _miCols) + col] = _txSize;
            }
        }
    }

    /// <summary><c>partition</c>'s CDF selection (spec §8.3.2).</summary>
    private int ReadPartitionSymbol(int r, int c, int bSize)
    {
        int ctx = PartitionContext(r, c, bSize, out int bsl);
        var cdf = bsl switch
        {
            1 => _cdf.PartitionW8[ctx],
            2 => _cdf.PartitionW16[ctx],
            3 => _cdf.PartitionW32[ctx],
            4 => _cdf.PartitionW64[ctx],
            _ => _cdf.PartitionW128[ctx],
        };

        return _s.ReadSymbol(cdf);
    }

    /// <summary><c>split_or_horz</c> (spec §8.3.2): a derived 2-symbol CDF built from the full <c>partition</c> CDF at this context.</summary>
    private bool ReadSplitOrHorz(int r, int c, int bSize)
    {
        int ctx = PartitionContext(r, c, bSize, out int bsl);
        var partitionCdf = SelectPartitionCdf(bsl, ctx);

        int psum =
            (partitionCdf[Av1PartitionType.Vert] - partitionCdf[Av1PartitionType.Vert - 1])
            + (partitionCdf[Av1PartitionType.Split] - partitionCdf[Av1PartitionType.Split - 1])
            + (partitionCdf[Av1PartitionType.HorzA] - partitionCdf[Av1PartitionType.HorzA - 1])
            + (partitionCdf[Av1PartitionType.VertA] - partitionCdf[Av1PartitionType.VertA - 1])
            + (partitionCdf[Av1PartitionType.VertB] - partitionCdf[Av1PartitionType.VertB - 1]);

        if (bSize != Av1BlockSize.Block128x128)
        {
            psum += partitionCdf[Av1PartitionType.Vert4] - partitionCdf[Av1PartitionType.Vert4 - 1];
        }

        Span<ushort> cdf = [(ushort)((1 << 15) - psum), 1 << 15, 0];
        return _s.ReadSymbol(cdf) != 0;
    }

    /// <summary><c>split_or_vert</c> (spec §8.3.2), the mirror image of <see cref="ReadSplitOrHorz"/>.</summary>
    private bool ReadSplitOrVert(int r, int c, int bSize)
    {
        int ctx = PartitionContext(r, c, bSize, out int bsl);
        var partitionCdf = SelectPartitionCdf(bsl, ctx);

        int psum =
            (partitionCdf[Av1PartitionType.Horz] - partitionCdf[Av1PartitionType.Horz - 1])
            + (partitionCdf[Av1PartitionType.Split] - partitionCdf[Av1PartitionType.Split - 1])
            + (partitionCdf[Av1PartitionType.HorzA] - partitionCdf[Av1PartitionType.HorzA - 1])
            + (partitionCdf[Av1PartitionType.HorzB] - partitionCdf[Av1PartitionType.HorzB - 1])
            + (partitionCdf[Av1PartitionType.VertA] - partitionCdf[Av1PartitionType.VertA - 1]);

        if (bSize != Av1BlockSize.Block128x128)
        {
            psum += partitionCdf[Av1PartitionType.Horz4] - partitionCdf[Av1PartitionType.Horz4 - 1];
        }

        Span<ushort> cdf = [(ushort)((1 << 15) - psum), 1 << 15, 0];
        return _s.ReadSymbol(cdf) != 0;
    }

    private ushort[] SelectPartitionCdf(int bsl, int ctx) => bsl switch
    {
        2 => _cdf.PartitionW16[ctx],
        3 => _cdf.PartitionW32[ctx],
        4 => _cdf.PartitionW64[ctx],
        _ => _cdf.PartitionW128[ctx],
    };

    /// <summary>The shared context derivation for <c>partition</c>/<c>split_or_horz</c>/<c>split_or_vert</c> (spec §8.3.2).</summary>
    private int PartitionContext(int r, int c, int bSize, out int bsl)
    {
        bsl = Av1BlockTables.MiWidthLog2[bSize];
        bool above = _availU && Av1BlockTables.MiWidthLog2[_miSizes[((r - 1) * _miCols) + c]] < bsl;
        bool left = _availL && Av1BlockTables.MiHeightLog2[_miSizes[(r * _miCols) + c - 1]] < bsl;
        return ((left ? 1 : 0) * 2) + (above ? 1 : 0);
    }

    /// <summary><c>is_inside(candidateR, candidateC)</c> (spec §5.11.51).</summary>
    private bool IsInside(int candidateR, int candidateC) =>
        candidateC >= _miColStart && candidateC < _miColEnd && candidateR >= _miRowStart && candidateR < _miRowEnd;

    // ----- Residual / coefficient decode (spec §5.11.34-§5.11.39) -----
    //
    // predict_intra/predict_chroma_from_luma/reconstruct (the pixel-producing steps transform_block()
    // would otherwise call) are intentionally omitted throughout this section: they're deterministic math
    // over already-decoded mode info / coefficients and consume zero entropy-coded bits, so skipping them
    // doesn't desynchronize anything -- see StoppedAtResidual's remarks.

    /// <summary><c>residual()</c> (spec §5.11.34), restricted to the intra path (<c>transform_tree()</c>'s inter-only recursive var-tx-size branch is never taken here).</summary>
    private void Residual(int bw4, int bh4)
    {
        _ = bw4;
        _ = bh4;

        int widthChunks = Math.Max(1, Av1BlockTables.BlockWidth(_miSize) >> 6);
        int heightChunks = Math.Max(1, Av1BlockTables.BlockHeight(_miSize) >> 6);
        int miSizeChunk = widthChunks > 1 || heightChunks > 1 ? Av1BlockSize.Block64x64 : _miSize;

        for (int chunkY = 0; chunkY < heightChunks; chunkY++)
        {
            for (int chunkX = 0; chunkX < widthChunks; chunkX++)
            {
                int miRowChunk = _miRow + (chunkY << 4);
                int miColChunk = _miCol + (chunkX << 4);

                int planeCount = _hasChroma ? 3 : 1;
                for (int plane = 0; plane < planeCount; plane++)
                {
                    int txSz = _lossless ? Av1TxSize.Tx4x4 : GetTxSizeForPlane(plane, _txSize);
                    int stepX = Av1TxDimensions.Width[txSz] >> 2;
                    int stepY = Av1TxDimensions.Height[txSz] >> 2;

                    int planeSz = Av1BlockTables.GetPlaneResidualSize(miSizeChunk, plane, _seq.SubsamplingX, _seq.SubsamplingY);
                    int num4x4W = Av1BlockTables.Num4x4BlocksWide[planeSz];
                    int num4x4H = Av1BlockTables.Num4x4BlocksHigh[planeSz];

                    int subX = plane > 0 && _seq.SubsamplingX ? 1 : 0;
                    int subY = plane > 0 && _seq.SubsamplingY ? 1 : 0;

                    int baseXBlock = (_miCol >> subX) * 4;
                    int baseYBlock = (_miRow >> subY) * 4;

                    for (int y = 0; y < num4x4H; y += stepY)
                    {
                        for (int x = 0; x < num4x4W; x += stepX)
                        {
                            TransformBlock(plane, baseXBlock, baseYBlock, txSz, x + ((chunkX << 4) >> subX), y + ((chunkY << 4) >> subY));
                        }
                    }
                }

                _ = miRowChunk;
                _ = miColChunk;
            }
        }
    }

    /// <summary><c>get_tx_size(plane, txSz)</c> (spec §5.11.37).</summary>
    private int GetTxSizeForPlane(int plane, int txSz)
    {
        if (plane == 0)
        {
            return txSz;
        }

        int uvTx = Av1BlockTables.MaxTxSizeRect[Av1BlockTables.GetPlaneResidualSize(_miSize, plane, _seq.SubsamplingX, _seq.SubsamplingY)];
        if (Av1TxDimensions.Width[uvTx] == 64 || Av1TxDimensions.Height[uvTx] == 64)
        {
            if (Av1TxDimensions.Width[uvTx] == 16)
            {
                return Av1TxSize.Tx16x32;
            }

            if (Av1TxDimensions.Height[uvTx] == 16)
            {
                return Av1TxSize.Tx32x16;
            }

            return Av1TxSize.Tx32x32;
        }

        return uvTx;
    }

    /// <summary><c>transform_block()</c> (spec §5.11.35), restricted to the intra (<c>!is_inter</c>) path and to non-palette blocks (palette is rejected during frame-header parsing).</summary>
    private void TransformBlock(int plane, int baseX, int baseY, int txSz, int x, int y)
    {
        int startX = baseX + (4 * x);
        int startY = baseY + (4 * y);

        int subX = plane > 0 && _seq.SubsamplingX ? 1 : 0;
        int subY = plane > 0 && _seq.SubsamplingY ? 1 : 0;

        int maxX = (_miCols * 4) >> subX;
        int maxY = (_miRows * 4) >> subY;
        if (startX >= maxX || startY >= maxY)
        {
            return;
        }

        int row = (startY << subY) >> 2;
        int col = (startX << subX) >> 2;
        int sbMask = _seq.Use128x128Superblock ? 31 : 15;
        int subBlockMiRow = row & sbMask;
        int subBlockMiCol = col & sbMask;
        int stepX = Av1TxDimensions.Width[txSz] >> 2;
        int stepY = Av1TxDimensions.Height[txSz] >> 2;

        bool isCfl = plane > 0 && _uvMode == Av1IntraMode.UvCflPred;
        int mode = plane == 0 ? _yMode : isCfl ? Av1IntraMode.DcPred : _uvMode;
        int log2W = Av1TxDimensions.WidthLog2[txSz];
        int log2H = Av1TxDimensions.HeightLog2[txSz];
        int w = 1 << log2W;
        int h = 1 << log2H;

        bool haveLeft = (plane == 0 ? _availL : _availLChroma) || x > 0;
        bool haveAbove = (plane == 0 ? _availU : _availUChroma) || y > 0;
        bool haveAboveRight = GetBlockDecoded(plane, (subBlockMiRow >> subY) - 1, (subBlockMiCol >> subX) + stepX);
        bool haveBelowLeft = GetBlockDecoded(plane, (subBlockMiRow >> subY) + stepY, (subBlockMiCol >> subX) - 1);

        Av1IntraPrediction.BuildEdges(
            _aboveRow, _leftCol,
            _planes[plane], _planeWidths[plane], startX, startY, w, h,
            haveLeft, haveAbove, haveAboveRight, haveBelowLeft, maxX - 1, maxY - 1, _seq.BitDepth);

        bool filterTypeSmooth = GetFilterType(plane);
        int angleDelta = plane == 0 ? _angleDeltaY : _angleDeltaUv;

        Av1IntraPrediction.Predict(
            _reconPred, w, h, log2W, log2H, _aboveRow, _leftCol, mode, haveLeft, haveAbove,
            plane == 0 && _useFilterIntra, _filterIntraMode, angleDelta, _seq.EnableIntraEdgeFilter,
            filterTypeSmooth, maxX - 1, maxY - 1, startX, startY, _seq.BitDepth);

        int stride = _planeWidths[plane];
        for (int i = 0; i < h; i++)
        {
            for (int j = 0; j < w; j++)
            {
                _planes[plane][((startY + i) * stride) + startX + j] = _reconPred[(i * w) + j];
            }
        }

        if (isCfl)
        {
            int alpha = plane == 1 ? _cflAlphaU : _cflAlphaV;
            Av1IntraPrediction.PredictChromaFromLuma(
                _planes[plane], _planeWidths[plane], _planes[0], _planeWidths[0],
                startX, startY, w, h, log2W, log2H, subX, subY, alpha, _maxLumaW, _maxLumaH, _seq.BitDepth);
        }

        if (plane == 0)
        {
            _maxLumaW = startX + (stepX * 4);
            _maxLumaH = startY + (stepY * 4);
        }

        if (!_skip)
        {
            int eob = Coeffs(plane, startX, startY, txSz);
            if (eob > 0)
            {
                Reconstruct(plane, startX, startY, txSz);
            }
        }

        int lfStride = _loopfilterTxSizeStrides[plane];
        for (int i = 0; i < stepY; i++)
        {
            for (int j = 0; j < stepX; j++)
            {
                SetBlockDecoded(plane, (subBlockMiRow >> subY) + i, (subBlockMiCol >> subX) + j, true);
                _loopfilterTxSizes[plane][(((row >> subY) + i) * lfStride) + (col >> subX) + j] = txSz;
            }
        }
    }

    /// <summary><c>get_filter_type(plane)</c> (spec §7.11.2.8).</summary>
    private bool GetFilterType(int plane)
    {
        bool aboveSmooth = false;
        bool leftSmooth = false;

        if (plane == 0 ? _availU : _availUChroma)
        {
            int r = _miRow - 1;
            int c = _miCol;
            if (plane > 0)
            {
                if (_seq.SubsamplingX && (_miCol & 1) == 0)
                {
                    c++;
                }

                if (_seq.SubsamplingY && (_miRow & 1) != 0)
                {
                    r--;
                }
            }

            aboveSmooth = IsSmooth(r, c, plane);
        }

        if (plane == 0 ? _availL : _availLChroma)
        {
            int r = _miRow;
            int c = _miCol - 1;
            if (plane > 0)
            {
                if (_seq.SubsamplingX && (_miCol & 1) != 0)
                {
                    c--;
                }

                if (_seq.SubsamplingY && (_miRow & 1) == 0)
                {
                    r++;
                }
            }

            leftSmooth = IsSmooth(r, c, plane);
        }

        return aboveSmooth || leftSmooth;
    }

    /// <summary><c>is_smooth(row, col, plane)</c> (spec §7.11.2.8), restricted to the intra path (the inter-only <c>RefFrames[row][col][0] &gt; INTRA_FRAME</c> early-return is never taken).</summary>
    private bool IsSmooth(int row, int col, int plane)
    {
        row = Math.Clamp(row, 0, _miRows - 1);
        col = Math.Clamp(col, 0, _miCols - 1);
        int mode = plane == 0 ? _yModes[(row * _miCols) + col] : _uvModes[(row * _miCols) + col];
        return mode is Av1IntraMode.SmoothPred or Av1IntraMode.SmoothVPred or Av1IntraMode.SmoothHPred;
    }

    /// <summary><c>get_qindex(0, segmentId)</c> (spec §7.12.2), always called with <c>ignoreDeltaQ = 0</c> (the only form <c>get_dc_quant</c>/<c>get_ac_quant</c> ever use).</summary>
    private int GetQIndex(int segmentId)
    {
        if (SegFeatureActive(segmentId, 0))
        {
            int data = _frame.Segmentation.FeatureData[segmentId, 0];
            int qindex = _frame.BaseQIdx + data;
            if (_frame.DeltaQPresent)
            {
                qindex = _currentQIndex + data;
            }

            return Math.Clamp(qindex, 0, 255);
        }

        return _frame.DeltaQPresent ? _currentQIndex : _frame.BaseQIdx;
    }

    /// <summary><c>get_dc_quant(plane)</c> (spec §7.12.2).</summary>
    private int GetDcQuant(int plane) => plane switch
    {
        0 => Av1Dequantizer.DcQ(GetQIndex(_segmentId) + _frame.DeltaQYDc, _seq.BitDepth),
        1 => Av1Dequantizer.DcQ(GetQIndex(_segmentId) + _frame.DeltaQUDc, _seq.BitDepth),
        _ => Av1Dequantizer.DcQ(GetQIndex(_segmentId) + _frame.DeltaQVDc, _seq.BitDepth),
    };

    /// <summary><c>get_ac_quant(plane)</c> (spec §7.12.2).</summary>
    private int GetAcQuant(int plane) => plane switch
    {
        0 => Av1Dequantizer.AcQ(GetQIndex(_segmentId), _seq.BitDepth),
        1 => Av1Dequantizer.AcQ(GetQIndex(_segmentId) + _frame.DeltaQUAc, _seq.BitDepth),
        _ => Av1Dequantizer.AcQ(GetQIndex(_segmentId) + _frame.DeltaQVAc, _seq.BitDepth),
    };

    /// <summary><c>reconstruct()</c> (spec §7.12.3): dequantize, invoke the 2D inverse transform, and add the (flip-adjusted) residual back into <see cref="_planes"/>.</summary>
    private void Reconstruct(int plane, int x, int y, int txSz)
    {
        int w = Av1TxDimensions.Width[txSz];
        int h = Av1TxDimensions.Height[txSz];

        // No Array.Clear needed here: Av1Dequantizer.Dequantize writes exactly [0,th)x[0,tw) (th/tw =
        // Min(32,h)/Min(32,w) for this same txSz), and Av1InverseTransform.Inverse2D's row loop only ever
        // reads dequant[(i*64)+j] when i<32 && j<32 -- for i<32 that's bounded by the same th/tw Dequantize
        // just wrote, and for i>=32 (only reachable when h>32) the read is skipped entirely (substituted
        // with 0), so a previous call's leftover values at any position this call doesn't read can never
        // surface. Confirmed by the byte-identical hash baseline before/after removing this clear.
        Av1Dequantizer.Dequantize(_quant, _reconDequant, txSz, GetDcQuant(plane), GetAcQuant(plane), _seq.BitDepth);
        Av1InverseTransform.Inverse2D(_reconDequant, _reconResidual, txSz, _planeTxType, _lossless, _seq.BitDepth);

        bool flipUd = _planeTxType is Av1TxType.FlipadstDct or Av1TxType.FlipadstAdst or Av1TxType.VFlipadst or Av1TxType.FlipadstFlipadst;
        bool flipLr = _planeTxType is Av1TxType.DctFlipadst or Av1TxType.AdstFlipadst or Av1TxType.HFlipadst or Av1TxType.FlipadstFlipadst;

        int stride = _planeWidths[plane];
        int maxSample = (1 << _seq.BitDepth) - 1;

        // The common case (no FLIPADST-involving transform type -- the overwhelming majority of blocks)
        // reduces to a plain contiguous add-then-clamp per row, vectorizable directly: unlike the flipped
        // case below, source (_reconResidual) and destination (_planes) indices advance together, so no
        // reversal is needed. Kept as a separate fast path rather than folding flip handling into the
        // vectorized loop, since a flip turns the destination into a reversed-stride write that Vector256
        // can't express as a single contiguous store.
        if (!flipUd && !flipLr)
        {
            var destPlane = _planes[plane];
            var zeroVec = Vector256<int>.Zero;
            var maxVec = Vector256.Create(maxSample);

            for (int i = 0; i < h; i++)
            {
                int rowBase = ((y + i) * stride) + x;
                int resBase = i * w;
                int j = 0;

                if (Vector256.IsHardwareAccelerated)
                {
                    for (; j + 8 <= w; j += 8)
                    {
                        var pred = Vector256.LoadUnsafe(ref destPlane[rowBase + j]);
                        var res = Vector256.LoadUnsafe(ref _reconResidual[resBase + j]);
                        var clamped = Vector256.Min(Vector256.Max(pred + res, zeroVec), maxVec);
                        clamped.StoreUnsafe(ref destPlane[rowBase + j]);
                    }
                }

                for (; j < w; j++)
                {
                    int idx = rowBase + j;
                    destPlane[idx] = Math.Clamp(destPlane[idx] + _reconResidual[resBase + j], 0, maxSample);
                }
            }

            return;
        }

        for (int i = 0; i < h; i++)
        {
            int yy = flipUd ? h - i - 1 : i;
            for (int j = 0; j < w; j++)
            {
                int xx = flipLr ? w - j - 1 : j;
                int idx = ((y + yy) * stride) + x + xx;
                _planes[plane][idx] = Math.Clamp(_planes[plane][idx] + _reconResidual[(i * w) + j], 0, maxSample);
            }
        }
    }

    /// <summary><c>coeffs(plane, startX, startY, txSz)</c> (spec §5.11.39): reads one transform block's coefficients into <see cref="_quant"/> and returns <c>eob</c>.</summary>
    private int Coeffs(int plane, int startX, int startY, int txSz)
    {
        int x4 = startX >> 2;
        int y4 = startY >> 2;
        int w4 = Av1TxDimensions.Width[txSz] >> 2;
        int h4 = Av1TxDimensions.Height[txSz] >> 2;
        int txSzCtx = (Av1CoeffTables.TxSizeSqr[txSz] + Av1CoeffTables.TxSizeSqrUp[txSz] + 1) >> 1;
        int ptype = plane > 0 ? 1 : 0;
        int segEob = txSz is Av1TxSize.Tx16x64 or Av1TxSize.Tx64x16 ? 512 : Math.Min(1024, Av1TxDimensions.Width[txSz] * Av1TxDimensions.Height[txSz]);

        Array.Clear(_quant, 0, segEob);

        int eob = 0;
        int culLevel = 0;
        int dcCategory = 0;

        int allZeroCtx = GetAllZeroContext(plane, txSz, x4, y4, w4, h4);
        bool allZero = _s.ReadSymbol(_cdf.TxbSkip[txSzCtx][allZeroCtx]) != 0;

        if (!allZero)
        {
            int txType = plane == 0 ? TransformType(x4, y4, txSz) : ComputeTxType(plane, txSz, x4, y4);
            _planeTxType = txType;
            int[] scan = Av1ScanTables.GetScan(txSz, txType);

            int eobMultisize = Math.Min(Av1TxDimensions.WidthLog2[txSz], 5) + Math.Min(Av1TxDimensions.HeightLog2[txSz], 5) - 4;
            int eobPt = ReadEobPt(eobMultisize, ptype, txType) + 1;

            eob = eobPt < 2 ? eobPt : (1 << (eobPt - 2)) + 1;
            int eobShift = Math.Max(-1, eobPt - 3);
            if (eobShift >= 0)
            {
                bool eobExtra = _s.ReadSymbol(_cdf.EobExtra[txSzCtx][ptype][eobPt - 3]) != 0;
                if (eobExtra)
                {
                    eob += 1 << eobShift;
                }

                for (int i = 1; i < Math.Max(0, eobPt - 2); i++)
                {
                    eobShift = Math.Max(0, eobPt - 2) - 1 - i;
                    if (_s.ReadLiteral(1) != 0)
                    {
                        eob += 1 << eobShift;
                    }
                }
            }

            for (int c = eob - 1; c >= 0; c--)
            {
                int pos = scan[c];
                int level;
                if (c == eob - 1)
                {
                    int ctx = GetCoeffBaseCtx(txSz, plane, x4, y4, pos, c, txType, isEob: true);
                    level = _s.ReadSymbol(_cdf.CoeffBaseEob[txSzCtx][ptype][ctx - Av1CoeffTables.SigCoefContexts + Av1CoeffTables.SigCoefContextsEob]) + 1;
                }
                else
                {
                    int ctx = GetCoeffBaseCtx(txSz, plane, x4, y4, pos, c, txType, isEob: false);
                    level = _s.ReadSymbol(_cdf.CoeffBase[txSzCtx][ptype][ctx]);
                }

                if (level > Av1CoeffTables.NumBaseLevels)
                {
                    int brCtx = GetCoeffBrCtx(txSz, plane, x4, y4, pos, txType);
                    var brCdf = _cdf.CoeffBr[Math.Min(txSzCtx, Av1TxSize.Tx32x32)][ptype][brCtx];
                    for (int idx = 0; idx < Av1CoeffTables.CoeffBaseRange / (Av1CoeffTables.BrCdfSize - 1); idx++)
                    {
                        int coeffBr = _s.ReadSymbol(brCdf);
                        level += coeffBr;
                        if (coeffBr < Av1CoeffTables.BrCdfSize - 1)
                        {
                            break;
                        }
                    }
                }

                _quant[pos] = level;
            }

            for (int c = 0; c < eob; c++)
            {
                int pos = scan[c];
                int sign;
                if (_quant[pos] != 0)
                {
                    if (c == 0)
                    {
                        int dcSignCtx = GetDcSignContext(plane, x4, y4, w4, h4);
                        sign = _s.ReadSymbol(_cdf.DcSign[ptype][dcSignCtx]);
                    }
                    else
                    {
                        sign = (int)_s.ReadLiteral(1);
                    }
                }
                else
                {
                    sign = 0;
                }

                if (_quant[pos] > Av1CoeffTables.NumBaseLevels + Av1CoeffTables.CoeffBaseRange)
                {
                    int length = 0;
                    bool golombLengthBit;
                    do
                    {
                        length++;
                        golombLengthBit = _s.ReadLiteral(1) != 0;
                        if (length > 20)
                        {
                            throw new AvifDecodingException("AV1 Exp-Golomb coefficient code exceeded the supported length (malformed or unsupported bitstream).");
                        }
                    }
                    while (!golombLengthBit);

                    int x = 1;
                    for (int i = length - 2; i >= 0; i--)
                    {
                        int golombDataBit = (int)_s.ReadLiteral(1);
                        x = (x << 1) | golombDataBit;
                    }

                    _quant[pos] = x + Av1CoeffTables.CoeffBaseRange + Av1CoeffTables.NumBaseLevels;
                }

                if (pos == 0 && _quant[pos] > 0)
                {
                    dcCategory = sign != 0 ? 1 : 2;
                }

                _quant[pos] &= 0xFFFFF;
                culLevel += _quant[pos];
                if (sign != 0)
                {
                    _quant[pos] = -_quant[pos];
                }
            }

            culLevel = Math.Min(63, culLevel);
        }

        for (int i = 0; i < w4; i++)
        {
            _aboveLevelContext[plane][x4 + i] = culLevel;
            _aboveDcContext[plane][x4 + i] = dcCategory;
        }

        for (int i = 0; i < h4; i++)
        {
            _leftLevelContext[plane][y4 + i] = culLevel;
            _leftDcContext[plane][y4 + i] = dcCategory;
        }

        _eobValue = eob;
        return eob;
    }

    /// <summary><c>eob_pt_*</c>'s shared reader, dispatching on <c>eobMultisize</c> (spec §5.11.39) with each size's own CDF context (spec §8.3.2: 2D transforms use context 0, horizontal-/vertical-only transforms use context 1).</summary>
    private int ReadEobPt(int eobMultisize, int ptype, int txType)
    {
        int ctx = Av1TxClass.Get(txType) == Av1TxClass.Class2D ? 0 : 1;

        return eobMultisize switch
        {
            0 => _s.ReadSymbol(_cdf.EobPt16[ptype][ctx]),
            1 => _s.ReadSymbol(_cdf.EobPt32[ptype][ctx]),
            2 => _s.ReadSymbol(_cdf.EobPt64[ptype][ctx]),
            3 => _s.ReadSymbol(_cdf.EobPt128[ptype][ctx]),
            4 => _s.ReadSymbol(_cdf.EobPt256[ptype][ctx]),
            5 => _s.ReadSymbol(_cdf.EobPt512[ptype]),
            _ => _s.ReadSymbol(_cdf.EobPt1024[ptype]),
        };
    }

    /// <summary><c>all_zero</c>'s CDF context derivation (spec §8.3.2).</summary>
    private int GetAllZeroContext(int plane, int txSz, int x4, int y4, int w4, int h4)
    {
        int maxX4 = _frame.MiCols;
        int maxY4 = _frame.MiRows;
        if (plane > 0)
        {
            maxX4 = _seq.SubsamplingX ? maxX4 >> 1 : maxX4;
            maxY4 = _seq.SubsamplingY ? maxY4 >> 1 : maxY4;
        }

        int w = Av1TxDimensions.Width[txSz];
        int h = Av1TxDimensions.Height[txSz];
        int bsize = Av1BlockTables.GetPlaneResidualSize(_miSize, plane, _seq.SubsamplingX, _seq.SubsamplingY);
        int bw = Av1BlockTables.BlockWidth(bsize);
        int bh = Av1BlockTables.BlockHeight(bsize);

        if (plane == 0)
        {
            int top = 0;
            int left = 0;
            for (int k = 0; k < w4; k++)
            {
                if (x4 + k < maxX4)
                {
                    top = Math.Max(top, _aboveLevelContext[plane][x4 + k]);
                }
            }

            for (int k = 0; k < h4; k++)
            {
                if (y4 + k < maxY4)
                {
                    left = Math.Max(left, _leftLevelContext[plane][y4 + k]);
                }
            }

            top = Math.Min(top, 255);
            left = Math.Min(left, 255);

            if (bw == w && bh == h)
            {
                return 0;
            }

            if (top == 0 && left == 0)
            {
                return 1;
            }

            if (top == 0 || left == 0)
            {
                return 2 + (Math.Max(top, left) > 3 ? 1 : 0);
            }

            if (Math.Max(top, left) <= 3)
            {
                return 4;
            }

            if (Math.Min(top, left) <= 3)
            {
                return 5;
            }

            return 6;
        }

        int above = 0;
        int leftAcc = 0;
        for (int i = 0; i < w4; i++)
        {
            if (x4 + i < maxX4)
            {
                above |= _aboveLevelContext[plane][x4 + i];
                above |= _aboveDcContext[plane][x4 + i];
            }
        }

        for (int i = 0; i < h4; i++)
        {
            if (y4 + i < maxY4)
            {
                leftAcc |= _leftLevelContext[plane][y4 + i];
                leftAcc |= _leftDcContext[plane][y4 + i];
            }
        }

        int ctx = (above != 0 ? 1 : 0) + (leftAcc != 0 ? 1 : 0);
        ctx += 7;
        if (bw * bh > w * h)
        {
            ctx += 3;
        }

        return ctx;
    }

    /// <summary><c>dc_sign</c>'s CDF context derivation (spec §8.3.2).</summary>
    private int GetDcSignContext(int plane, int x4, int y4, int w4, int h4)
    {
        int maxX4 = _frame.MiCols;
        int maxY4 = _frame.MiRows;
        if (plane > 0)
        {
            maxX4 = _seq.SubsamplingX ? maxX4 >> 1 : maxX4;
            maxY4 = _seq.SubsamplingY ? maxY4 >> 1 : maxY4;
        }

        int dcSign = 0;
        for (int k = 0; k < w4; k++)
        {
            if (x4 + k < maxX4)
            {
                int sign = _aboveDcContext[plane][x4 + k];
                if (sign == 1)
                {
                    dcSign--;
                }
                else if (sign == 2)
                {
                    dcSign++;
                }
            }
        }

        for (int k = 0; k < h4; k++)
        {
            if (y4 + k < maxY4)
            {
                int sign = _leftDcContext[plane][y4 + k];
                if (sign == 1)
                {
                    dcSign--;
                }
                else if (sign == 2)
                {
                    dcSign++;
                }
            }
        }

        if (dcSign < 0)
        {
            return 1;
        }

        if (dcSign > 0)
        {
            return 2;
        }

        return 0;
    }

    /// <summary><c>coeff_base</c>/<c>coeff_base_eob</c>'s shared context derivation, <c>get_coeff_base_ctx()</c> (spec §8.3.2).</summary>
    private int GetCoeffBaseCtx(int txSz, int plane, int blockX, int blockY, int pos, int c, int txType, bool isEob)
    {
        _ = blockX;
        _ = blockY;
        int adjTxSz = Av1CoeffTables.AdjustedTxSize[txSz];
        int bwl = Av1TxDimensions.WidthLog2[adjTxSz];
        int width = 1 << bwl;
        int height = Av1TxDimensions.Height[adjTxSz];

        if (isEob)
        {
            if (c == 0)
            {
                return Av1CoeffTables.SigCoefContexts - 4;
            }

            if (c <= (height << bwl) / 8)
            {
                return Av1CoeffTables.SigCoefContexts - 3;
            }

            if (c <= (height << bwl) / 4)
            {
                return Av1CoeffTables.SigCoefContexts - 2;
            }

            return Av1CoeffTables.SigCoefContexts - 1;
        }

        int txClass = Av1TxClass.Get(txType);
        int row = pos >> bwl;
        int col = pos - (row << bwl);
        int mag = 0;

        for (int idx = 0; idx < Av1CoeffTables.SigRefDiffOffsetNum; idx++)
        {
            int refRow = row + Av1CoeffTables.SigRefDiffOffset[txClass][idx][0];
            int refCol = col + Av1CoeffTables.SigRefDiffOffset[txClass][idx][1];
            if (refRow >= 0 && refCol >= 0 && refRow < height && refCol < width)
            {
                mag += Math.Min(Math.Abs(_quant[(refRow << bwl) + refCol]), 3);
            }
        }

        int ctx = Math.Min((mag + 1) >> 1, 4);

        if (txClass == Av1TxClass.Class2D)
        {
            if (row == 0 && col == 0)
            {
                return 0;
            }

            return ctx + Av1CoeffTables.CoeffBaseCtxOffset[txSz][Math.Min(row, 4)][Math.Min(col, 4)];
        }

        int posIdx = txClass == Av1TxClass.ClassVert ? row : col;
        return ctx + Av1CoeffTables.CoeffBasePosCtxOffset[Math.Min(posIdx, 2)];
    }

    /// <summary><c>coeff_br</c>'s CDF context derivation (spec §8.3.2).</summary>
    private int GetCoeffBrCtx(int txSz, int plane, int x4, int y4, int pos, int txType)
    {
        int adjTxSz = Av1CoeffTables.AdjustedTxSize[txSz];
        int bwl = Av1TxDimensions.WidthLog2[adjTxSz];
        int txw = Av1TxDimensions.Width[adjTxSz];
        int txh = Av1TxDimensions.Height[adjTxSz];
        int row = pos >> bwl;
        int col = pos - (row << bwl);
        int mag = 0;
        int txClass = Av1TxClass.Get(txType);

        _ = plane;
        _ = x4;
        _ = y4;

        for (int idx = 0; idx < 3; idx++)
        {
            int refRow = row + Av1CoeffTables.MagRefOffsetWithTxClass[txClass][idx][0];
            int refCol = col + Av1CoeffTables.MagRefOffsetWithTxClass[txClass][idx][1];
            if (refRow >= 0 && refCol >= 0 && refRow < txh && refCol < (1 << bwl))
            {
                mag += Math.Min(_quant[(refRow * txw) + refCol], Av1CoeffTables.CoeffBaseRange + Av1CoeffTables.NumBaseLevels + 1);
            }
        }

        mag = Math.Min((mag + 1) >> 1, 6);

        if (pos == 0)
        {
            return mag;
        }

        if (txClass == Av1TxClass.Class2D)
        {
            return row < 2 && col < 2 ? mag + 7 : mag + 14;
        }

        if (txClass == Av1TxClass.ClassHoriz)
        {
            return col == 0 ? mag + 7 : mag + 14;
        }

        return row == 0 ? mag + 7 : mag + 14;
    }

    /// <summary><c>compute_tx_type(plane, txSz, blockX, blockY)</c> (spec §5.11.40), restricted to the intra path. <paramref name="blockX"/>/<paramref name="blockY"/> are part of the spec's signature (used by the inter-only path this decoder never reaches) and unused here.</summary>
    private int ComputeTxType(int plane, int txSz, int blockX, int blockY)
    {
        _ = blockX;
        _ = blockY;

        int txSzSqrUp = Av1CoeffTables.TxSizeSqrUp[txSz];
        if (_lossless || txSzSqrUp > Av1TxSize.Tx32x32)
        {
            return Av1TxType.DctDct;
        }

        int txSet = GetTxSet(txSz);

        if (plane == 0)
        {
            return _planeTxType;
        }

        int txType = Av1TxTypeTables.ModeToTxfm[_uvMode];
        return IsTxTypeInSetIntra(txSet, txType) ? txType : Av1TxType.DctDct;
    }

    private static bool IsTxTypeInSetIntra(int txSet, int txType) => Av1TxTypeTables.TxTypeInSetIntra[txSet][txType];

    /// <summary><c>transform_type(x4, y4, txSz)</c> (spec §5.11.47), restricted to the intra path. Returns the selected <see cref="Av1TxType"/> directly rather than writing the spec's frame-sized <c>TxTypes[][]</c> array -- see this section's header remarks for why nothing else needs to read it back from elsewhere in this decoder's current (entropy-decode-only) scope.</summary>
    private int TransformType(int x4, int y4, int txSz)
    {
        _ = x4;
        _ = y4;

        int set = GetTxSet(txSz);
        int qindex = _frame.Segmentation.Enabled ? GetQIndexForSegment(_segmentId) : _frame.BaseQIdx;

        if (set <= 0 || qindex <= 0)
        {
            return Av1TxType.DctDct;
        }

        int intraDir = _useFilterIntra ? Av1TxTypeTables.FilterIntraModeToIntraDir[_filterIntraMode] : _yMode;
        var cdf = set == Av1TxSet.Intra1 ? _cdf.IntraTxTypeSet1[Av1CoeffTables.TxSizeSqr[txSz]][intraDir] : _cdf.IntraTxTypeSet2[Av1CoeffTables.TxSizeSqr[txSz]][intraDir];
        int intraTxType = _s.ReadSymbol(cdf);

        return set == Av1TxSet.Intra1 ? Av1TxTypeTables.TxTypeIntraInvSet1[intraTxType] : Av1TxTypeTables.TxTypeIntraInvSet2[intraTxType];
    }

    private int GetQIndexForSegment(int segmentId)
    {
        if (_frame.Segmentation.Enabled && _frame.Segmentation.FeatureEnabled[segmentId, 0])
        {
            int data = _frame.Segmentation.FeatureData[segmentId, 0];
            return Math.Clamp(_frame.BaseQIdx + data, 0, 255);
        }

        return _frame.BaseQIdx;
    }

    /// <summary><c>get_tx_set(txSz)</c> (spec §5.11.48), restricted to the intra path.</summary>
    private int GetTxSet(int txSz)
    {
        int txSzSqr = Av1CoeffTables.TxSizeSqr[txSz];
        int txSzSqrUp = Av1CoeffTables.TxSizeSqrUp[txSz];

        if (txSzSqrUp > Av1TxSize.Tx32x32)
        {
            return Av1TxSet.DctOnly;
        }

        if (txSzSqrUp == Av1TxSize.Tx32x32)
        {
            return Av1TxSet.DctOnly;
        }

        if (_frame.ReducedTxSet)
        {
            return Av1TxSet.Intra2;
        }

        if (txSzSqr == Av1TxSize.Tx16x16)
        {
            return Av1TxSet.Intra2;
        }

        return Av1TxSet.Intra1;
    }
}

/// <summary>Per-transform-size pixel dimensions (spec §9.3, <c>Tx_Width</c>/<c>Tx_Height</c>), indexed by <see cref="Av1TxSize"/>.</summary>
internal static class Av1TxDimensions
{
    public static readonly int[] Width = [4, 8, 16, 32, 64, 4, 8, 8, 16, 16, 32, 32, 64, 4, 16, 8, 32, 16, 64];

    public static readonly int[] Height = [4, 8, 16, 32, 64, 8, 4, 16, 8, 32, 16, 64, 32, 16, 4, 32, 8, 64, 16];

    /// <summary><c>Tx_Width_Log2</c> (spec §9.3), extracted directly from the specification text.</summary>
    public static readonly int[] WidthLog2 = [2, 3, 4, 5, 6, 2, 3, 3, 4, 4, 5, 5, 6, 2, 4, 3, 5, 4, 6];

    /// <summary><c>Tx_Height_Log2</c> (spec §9.3), extracted directly from the specification text.</summary>
    public static readonly int[] HeightLog2 = [2, 3, 4, 5, 6, 3, 2, 4, 3, 5, 4, 6, 5, 4, 2, 5, 3, 6, 4];
}

/// <summary>The maximum transform depth for each block size (spec §5.11.15), extracted directly from the specification text.</summary>
internal static class Av1MaxTxDepth
{
    public static readonly int[] Values = [0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 4, 4, 4, 2, 2, 3, 3, 4, 4];
}
