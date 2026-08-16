namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// The outcome of <see cref="Av1FrameDecoder.Decode"/>. Pixel reconstruction doesn't exist yet, so a
/// successful call always means decode reached (and stopped at) the first coefficient/residual read --
/// <see cref="BlocksDecoded"/> and <see cref="Sequence"/>/<see cref="Frame"/> let callers (mainly tests)
/// verify the partition tree and mode-info decode actually ran meaningfully rather than failing
/// immediately.
/// </summary>
internal sealed class Av1FrameDecodeResult
{
    public required Av1SequenceHeader Sequence { get; init; }

    public required Av1FrameHeader Frame { get; init; }

    /// <summary>How many leaf blocks were fully decoded (partition + mode info + tx size) before hitting the residual-decode boundary.</summary>
    public required int BlocksDecoded { get; init; }

    /// <summary>How many tiles were processed.</summary>
    public required int TilesStarted { get; init; }

    /// <summary>
    /// Reconstructed sample planes (spec's <c>CurrFrame</c>), one flat row-major buffer per plane (Y, then
    /// U/V when <see cref="Av1SequenceHeader.MonoChrome"/> is false), each exactly <see cref="PlaneWidths"/>
    /// x <see cref="PlaneHeights"/> for that plane -- no border padding. Samples are plain integers in
    /// <c>[0, (1 &lt;&lt; BitDepth) - 1]</c>, not yet packed into any particular byte layout.
    /// </summary>
    public required int[][] Planes { get; init; }

    public required int[] PlaneWidths { get; init; }

    public required int[] PlaneHeights { get; init; }

    /// <summary>Whether decode reached the (not-yet-implemented) residual/coefficient stage -- true for every real file today, since that stage doesn't exist yet; kept explicit rather than assumed so callers don't have to guess why decode stopped where it did.</summary>
    public required bool StoppedAtResidual { get; init; }

    /// <summary>Per-mi-position mode/skip/segment neighbor state (flat <c>[row * Frame.MiCols + col]</c>), consumed by the in-loop filter passes.</summary>
    public required int[] YModes { get; init; }

    public required int[] MiSizes { get; init; }

    public required bool[] Skips { get; init; }

    public required int[] SegmentIds { get; init; }

    /// <summary>Per-mi-position <c>DeltaLF</c> snapshot at block-decode time (spec's <c>DeltaLFs[row][col][i]</c>), one flat <c>[row * Frame.MiCols + col]</c> array per of the 4 <c>FRAME_LF_COUNT</c> slots.</summary>
    public required int[][] DeltaLfs { get; init; }

    /// <summary>Per-plane transform size used by the deblocking filter (spec's <c>LoopfilterTxSizes[plane][...]</c>), each indexed in that plane's own (possibly subsampled) mi grid with row stride <see cref="LoopfilterTxSizeStrides"/>.</summary>
    public required int[][] LoopfilterTxSizes { get; init; }

    public required int[] LoopfilterTxSizeStrides { get; init; }

    /// <summary>Per-64x64-luma-unit CDEF strength-set index (spec's <c>cdef_idx[r][c]</c>), flat <c>[row * Frame.MiCols + col]</c>, sparsely written (only at 64x64-mi-aligned positions).</summary>
    public required int[] CdefIdx { get; init; }

    /// <summary>Per-plane loop restoration unit grid; <see langword="null"/> for a plane whose <c>FrameRestorationType</c> is <c>RESTORE_NONE</c>.</summary>
    public required Av1RestorationUnitGrid?[] RestorationUnits { get; init; }

    /// <summary>The smallest (most negative) <c>SymbolMaxBits</c> across every tile at <c>exit_symbol()</c> time. Spec §8.2.4 requires this to be &gt;= -14 for bitstream conformance -- see <see cref="Av1TileDecoder.SymbolMaxBitsAtExit"/>'s remarks for why this is such a strong correctness signal.</summary>
    public required int MinSymbolMaxBitsAtExit { get; init; }
}

/// <summary>
/// Top-level per-item AV1 decode orchestrator (direct analog of <c>Vp8FrameDecoder</c>): parses the
/// sequence header and (the single, since AVIF still images have exactly one frame) frame header, resolves
/// tile boundaries via <c>tile_group_obu()</c>, and runs each tile's partition tree + mode-info decode via
/// <see cref="Av1TileDecoder"/>. Coefficient/residual decode and reconstruction (turning mode info into
/// actual pixels) are not implemented yet, so every real call today returns with
/// <see cref="Av1FrameDecodeResult.StoppedAtResidual"/> true -- this method still throws
/// <see cref="AvifDecodingException"/>/<see cref="AvifUnsupportedFeatureException"/> for genuine
/// container/bitstream problems (missing OBUs, malformed headers, out-of-scope features), just not for
/// "residual decode doesn't exist yet", which is a known, inspectable stopping point rather than a
/// failure.
/// </summary>
internal static class Av1FrameDecoder
{
    public static Av1FrameDecodeResult Decode(byte[] tileBytes)
    {
        var obus = Av1ObuReader.ReadObus(tileBytes, 0, tileBytes.Length);

        Av1SequenceHeader? sequence = null;

        for (int i = 0; i < obus.Count; i++)
        {
            var obu = obus[i];

            if (obu.Type == Av1ObuType.SequenceHeader)
            {
                sequence = Av1SequenceHeader.Parse(new Av1BitReader(tileBytes, obu.PayloadOffset, obu.PayloadLength));
                continue;
            }

            if (obu.Type is Av1ObuType.Frame or Av1ObuType.FrameHeader)
            {
                if (sequence is null)
                {
                    throw new AvifDecodingException("AV1 frame header OBU appeared before any sequence header OBU.");
                }

                var reader = new Av1BitReader(tileBytes, obu.PayloadOffset, obu.PayloadLength);
                var frame = Av1FrameHeader.Parse(reader, sequence);
                reader.ByteAlign();

                if (obu.Type == Av1ObuType.Frame)
                {
                    // frame_obu(): frame_header_obu() + byte_alignment() + tile_group_obu(sz), all within
                    // this one OBU's payload -- the tile group data starts right where the header parse
                    // (now byte-aligned) left off.
                    int tileGroupOffset = reader.BytePosition;
                    int tileGroupLength = obu.PayloadOffset + obu.PayloadLength - tileGroupOffset;
                    return DecodeTileGroup(tileBytes, sequence, frame, tileGroupOffset, tileGroupLength);
                }

                // OBU_FRAME_HEADER: the tile group is a separate, later OBU_TILE_GROUP.
                for (int j = i + 1; j < obus.Count; j++)
                {
                    if (obus[j].Type == Av1ObuType.TileGroup)
                    {
                        return DecodeTileGroup(tileBytes, sequence, frame, obus[j].PayloadOffset, obus[j].PayloadLength);
                    }
                }

                throw new AvifDecodingException("AV1 bitstream has a frame header OBU but no following tile group OBU.");
            }
        }

        throw new AvifDecodingException("AV1 bitstream is missing a sequence header or frame header OBU.");
    }

    /// <summary>Parses <c>tile_group_obu()</c> and runs every tile's partition/mode-info decode. Frame-sized neighbor context arrays are shared across tiles -- see <see cref="Av1TileDecoder"/>'s remarks for why that's safe.</summary>
    private static Av1FrameDecodeResult DecodeTileGroup(byte[] data, Av1SequenceHeader sequence, Av1FrameHeader frame, int tileGroupOffset, int tileGroupLength)
    {
        var tileRanges = Av1TileGroupObu.Parse(data, tileGroupOffset, tileGroupLength, frame.TileInfo);

        int frameSize = frame.MiRows * frame.MiCols;
        var yModes = new int[frameSize];
        var uvModes = new int[frameSize];
        var miSizes = new int[frameSize];
        var skips = new bool[frameSize];
        var segmentIds = new int[frameSize];
        var interTxSizes = new int[frameSize];

        // Reconstructed-plane buffers are allocated to the superblock-aligned canvas, not just
        // MiCols*4/MiRows*4: a coding/transform block chosen by decode_partition() is only guaranteed to
        // START within [0, MiCols)x[0, MiRows), never to fully fit within it -- its nominal pixel extent
        // routinely overhangs into the superblock's padding region at the frame's right/bottom edge (the
        // same reason real AV1 decoders always over-allocate to superblock granularity). MiCols*4/MiRows*4
        // remain the correct bound for every spec-level maxX/maxY computation (edge-sample clamping,
        // transform_block's own early return) -- only the backing storage needs the larger size.
        int sbSizePixels = sequence.Use128x128Superblock ? 128 : 64;
        int alignedLumaW = ((frame.MiCols * 4) + sbSizePixels - 1) / sbSizePixels * sbSizePixels;
        int alignedLumaH = ((frame.MiRows * 4) + sbSizePixels - 1) / sbSizePixels * sbSizePixels;

        int numPlanes = sequence.NumPlanes;
        var planeWidths = new int[3];
        var planeHeights = new int[3];
        var planes = new int[3][];
        for (int plane = 0; plane < 3; plane++)
        {
            int subX = plane > 0 && sequence.SubsamplingX ? 1 : 0;
            int subY = plane > 0 && sequence.SubsamplingY ? 1 : 0;
            planeWidths[plane] = plane < numPlanes ? alignedLumaW >> subX : 0;
            planeHeights[plane] = plane < numPlanes ? alignedLumaH >> subY : 0;
            planes[plane] = new int[planeWidths[plane] * planeHeights[plane]];
        }

        var deltaLfs = new int[4][];
        for (int i = 0; i < 4; i++)
        {
            deltaLfs[i] = new int[frameSize];
        }

        var cdefIdx = new int[frameSize];

        // Sized to the superblock-aligned mi grid (matching alignedLumaW/H above), not just
        // MiCols/MiRows: transform_block() writes LoopfilterTxSizes at the same (row>>subY, col>>subX)
        // positions its own overhang can reach beyond the frame's mi bounds -- see the plane-buffer
        // comment above for why that overhang is legitimate, not a bug.
        int alignedMiCols = alignedLumaW / 4;
        int alignedMiRows = alignedLumaH / 4;

        var loopfilterTxSizes = new int[3][];
        var loopfilterTxSizeStrides = new int[3];
        for (int plane = 0; plane < 3; plane++)
        {
            int subX = plane > 0 && sequence.SubsamplingX ? 1 : 0;
            int subY = plane > 0 && sequence.SubsamplingY ? 1 : 0;
            int strideCols = alignedMiCols >> subX;
            int strideRows = alignedMiRows >> subY;
            loopfilterTxSizeStrides[plane] = strideCols;
            loopfilterTxSizes[plane] = plane < numPlanes ? new int[strideCols * strideRows] : [];
        }

        var restorationUnits = new Av1RestorationUnitGrid?[3];
        for (int plane = 0; plane < numPlanes; plane++)
        {
            if (frame.LoopRestoration.FrameRestorationType[plane] == Av1LoopRestorationParams.RestoreNone)
            {
                continue;
            }

            int subX = plane > 0 && sequence.SubsamplingX ? 1 : 0;
            int subY = plane > 0 && sequence.SubsamplingY ? 1 : 0;
            int unitSize = frame.LoopRestoration.UnitSize[plane];
            int frameSizeForRows = Round2(frame.FrameHeight, subY);
            int frameSizeForCols = Round2(frame.UpscaledWidth, subX);
            restorationUnits[plane] = Av1RestorationUnitGrid.Create(unitSize, frameSizeForRows, frameSizeForCols);
        }

        int blocksDecoded = 0;
        int tilesStarted = 0;
        bool stoppedAtResidual = false;
        int minSymbolMaxBitsAtExit = int.MaxValue;

        foreach (var tileRange in tileRanges)
        {
            tilesStarted++;

            int miRowStart = frame.TileInfo.MiRowStarts[tileRange.TileRow];
            int miRowEnd = frame.TileInfo.MiRowStarts[tileRange.TileRow + 1];
            int miColStart = frame.TileInfo.MiColStarts[tileRange.TileCol];
            int miColEnd = frame.TileInfo.MiColStarts[tileRange.TileCol + 1];

            var cdf = new Av1CdfContext(frame.BaseQIdx);
            var symbols = new Av1SymbolDecoder(data, tileRange.Offset, tileRange.Length, frame.DisableCdfUpdate);
            var tileDecoder = new Av1TileDecoder(
                symbols, cdf, sequence, frame, miRowStart, miRowEnd, miColStart, miColEnd,
                yModes, uvModes, miSizes, skips, segmentIds, interTxSizes, planes, planeWidths, planeHeights,
                deltaLfs, cdefIdx, loopfilterTxSizes, loopfilterTxSizeStrides, restorationUnits);

            tileDecoder.DecodeTile();

            blocksDecoded += tileDecoder.BlocksDecoded;
            stoppedAtResidual |= tileDecoder.StoppedAtResidual;
            minSymbolMaxBitsAtExit = Math.Min(minSymbolMaxBitsAtExit, tileDecoder.SymbolMaxBitsAtExit);
        }

        var result = new Av1FrameDecodeResult
        {
            Sequence = sequence,
            Frame = frame,
            BlocksDecoded = blocksDecoded,
            TilesStarted = tilesStarted,
            StoppedAtResidual = stoppedAtResidual,
            MinSymbolMaxBitsAtExit = minSymbolMaxBitsAtExit,
            Planes = planes,
            PlaneWidths = planeWidths,
            PlaneHeights = planeHeights,
            YModes = yModes,
            MiSizes = miSizes,
            Skips = skips,
            SegmentIds = segmentIds,
            DeltaLfs = deltaLfs,
            LoopfilterTxSizes = loopfilterTxSizes,
            LoopfilterTxSizeStrides = loopfilterTxSizeStrides,
            CdefIdx = cdefIdx,
            RestorationUnits = restorationUnits,
        };

        Av1DeblockingFilter.Apply(result);

        // Only loop restoration ever reads this pre-CDEF snapshot (spec §7.17.1's stripe-boundary rule);
        // Av1LoopRestoration.Apply returns immediately when UsesLr is false without touching it, so
        // cloning three full planes (~12MB at 1080p) here would be pure waste for the (common) no-LR case.
        var deblockedPlanes = frame.LoopRestoration.UsesLr ? new int[3][] : [];
        if (frame.LoopRestoration.UsesLr)
        {
            for (int plane = 0; plane < numPlanes; plane++)
            {
                deblockedPlanes[plane] = (int[])result.Planes[plane].Clone();
            }
        }

        Av1Cdef.Apply(result);
        Av1LoopRestoration.Apply(result, deblockedPlanes);

        return result;
    }

    private static int Round2(int x, int n) => n == 0 ? x : (x + (1 << (n - 1))) >> n;
}
