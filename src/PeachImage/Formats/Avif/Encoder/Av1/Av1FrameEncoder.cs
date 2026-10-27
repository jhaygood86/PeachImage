namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Top-level AV1 encode entry point: converts a source image to YUV, pads it (edge-replicated -- see
/// <see cref="Av1TileEncoder"/>'s remarks on why this is required for its simplified, edge-case-free
/// superblock traversal) to a coded canvas that's a multiple of the actual superblock size -- 128 pixels for
/// lossless (128x128 superblocks), 64 for everything else -- writes the sequence/frame headers and the single tile,
/// and assembles the final OBU byte stream (temporal delimiter + sequence header + frame header + tile
/// group). The true (unpadded) source dimensions are carried separately in the returned
/// <see cref="Av1EncodedFrame"/> for the container writer's <c>ispe</c> box -- the AVIF container crops the
/// padded coded frame back down to the true size at decode time (see <c>AvifDecoder.Decode</c>'s use of the
/// container's own width/height, not the AV1 bitstream's <c>RenderWidth</c>/<c>RenderHeight</c>).
/// </summary>
internal static class Av1FrameEncoder
{
    /// <summary>
    /// Encodes an opaque 8-bit RGB24 (or, if <paramref name="monoChrome"/>, Gray8) source into a full AV1
    /// OBU byte stream. When <paramref name="lossless"/> is <see langword="true"/>, <paramref name="quality"/>
    /// is ignored entirely and every block is coded via AV1's lossless Walsh-Hadamard path instead of DCT_DCT
    /// quantization (<c>base_q_idx</c> forced to 0, AV1's coded-lossless trigger, rather than derived from
    /// <paramref name="quality"/>).
    /// </summary>
    public static Av1EncodedFrame Encode(ReadOnlySpan<byte> pixels, int width, int height, bool monoChrome, int quality, bool lossless = false, int effort = 2, Action<Av1BlockDecisionRecord>? onLeafCommitted = null, int? tileRowsLog2 = null, int maxDegreeOfParallelism = -1)
    {
        // Lossless uses 128x128 superblocks (Av1SequenceHeaderWriter/Av1TileEncoder), so the coded canvas
        // must pad to a 128-pixel multiple instead of 64 -- same "every superblock fully in-bounds" reason
        // the 64-pixel padding exists for the non-lossless/64x64-superblock path (see this class's own
        // remarks and Av1TileEncoder's).
        int sbPixels = lossless ? 128 : 64;
        int paddedWidth = ((width + sbPixels - 1) / sbPixels) * sbPixels;
        int paddedHeight = ((height + sbPixels - 1) / sbPixels) * sbPixels;

        // The one gate for genuinely lossless RGB: lossless + real chroma planes means 4:4:4 with an
        // identity color matrix (Av1RgbToYuvIdentityConverter) instead of 4:2:0 BT.601 -- see that class's
        // remarks for why only the identity matrix is exactly invertible. Always false for monoChrome (no
        // chroma planes to subsample either way), so this can never change monoChrome/alpha item output.
        bool chroma444 = lossless && !monoChrome;

        int[] yPlane;
        int[]? uPlane = null;
        int[]? vPlane = null;
        int chromaWidth = 0;
        int chromaHeight = 0;
        int paddedChromaWidth = 0;
        int paddedChromaHeight = 0;

        if (monoChrome)
        {
            int[] y = Av1RgbToYuvConverter.ConvertMonoChrome(pixels, width, height);
            yPlane = PadPlane(y, width, height, paddedWidth, paddedHeight);
        }
        else if (chroma444)
        {
            var (y, u, v) = Av1RgbToYuvIdentityConverter.Convert(pixels, width, height);
            chromaWidth = width;
            chromaHeight = height;
            paddedChromaWidth = paddedWidth;
            paddedChromaHeight = paddedHeight;

            yPlane = PadPlane(y, width, height, paddedWidth, paddedHeight);
            uPlane = PadPlane(u, chromaWidth, chromaHeight, paddedChromaWidth, paddedChromaHeight);
            vPlane = PadPlane(v, chromaWidth, chromaHeight, paddedChromaWidth, paddedChromaHeight);
        }
        else
        {
            var (y, u, v, cw, ch) = Av1RgbToYuvConverter.Convert(pixels, width, height);
            chromaWidth = cw;
            chromaHeight = ch;
            paddedChromaWidth = paddedWidth / 2;
            paddedChromaHeight = paddedHeight / 2;

            yPlane = PadPlane(y, width, height, paddedWidth, paddedHeight);
            uPlane = PadPlane(u, chromaWidth, chromaHeight, paddedChromaWidth, paddedChromaHeight);
            vPlane = PadPlane(v, chromaWidth, chromaHeight, paddedChromaWidth, paddedChromaHeight);
        }

        int baseQIdx = lossless ? 0 : Av1ForwardQuantizer.QualityToBaseQIdx(quality);

        // Real, content-based decision (Av1ScreenContentEstimator) for whether palette/IntraBC are worth
        // structurally enabling at all -- only ever computed for lossless (this encoder's only mode that
        // implements either tool). Sampled directly off the padded yPlane at the TRUE (width, height) bounds:
        // padding only ever replicates the last real row/column past those bounds, so this never samples a
        // padding pixel regardless of stride. AllowIntrabc is a real, separately-computed decision from
        // AllowScreenContentTools (spec requires both for IntraBC; a stricter, higher-variance threshold than
        // palette's own -- see Av1ScreenContentEstimator's remarks), never true unless lossless also is.
        //
        // This static heuristic is only ever a coarse, cheap-to-compute PROXY for whether enabling these
        // tools structurally actually pays for itself -- confirmed via this project's own libaom byte-exact
        // comparison harness that real encoders (aomenc's own av1_determine_sc_tools_with_encoding) don't
        // trust an equivalent heuristic either: they run an actual trial sub-encode with and without screen
        // content tools and measure the real result. This project's own equivalent (below) is simpler (a
        // full-fidelity trial via the real encoder, not aomenc's own reduced-fidelity fixed-partition one --
        // this project's stated priority is size over speed, and full fidelity means the trial's own real
        // byte count is exactly what would actually ship, not an approximation of it) but the same idea: the
        // heuristic only decides which candidates are even worth *trying*, never the final answer by itself.
        bool heuristicScreenContentTools = false;
        bool heuristicIntrabc = false;
        if (lossless)
        {
            (heuristicScreenContentTools, heuristicIntrabc) = Av1ScreenContentEstimator.Estimate(yPlane, paddedWidth, width, height);
        }

        var reconY = new int[paddedWidth * paddedHeight];
        int[]? reconU = monoChrome ? null : new int[paddedChromaWidth * paddedChromaHeight];
        int[]? reconV = monoChrome ? null : new int[paddedChromaWidth * paddedChromaHeight];

        // The bitstream's own signaled frame size: TRUE (unpadded) dimensions for lossless -- confirmed via
        // this project's own libaom byte-exact comparison harness that real encoders never round
        // FrameWidth/FrameHeight/MiCols/MiRows up to a superblock multiple (AV1 partitioning natively
        // supports a coding block whose nominal extent overhangs past the frame's true bounds -- see
        // Av1TileEncoder.EncodeTile's own remarks on TrueMiCols/TrueMiRows and the hasRows/hasCols-restricted
        // partition-type legality this requires). Deliberately still the PADDED dimensions for non-lossless:
        // that path's own partition/mode-info commit logic was never updated to handle a real frame-edge
        // overhang the way the lossless path now is, so signaling true dimensions there without also fixing
        // that logic would desync a real decoder rather than just being suboptimal. Passing paddedWidth/
        // paddedHeight here for non-lossless makes TrueMiCols/TrueMiRows equal the already-superblock-aligned
        // MiCols/MiRows exactly, so hasRows/hasCols are always true and the new restriction logic is a
        // guaranteed no-op there -- this one conditional is the only place non-lossless's existing,
        // already-tested behavior needs to be preserved explicitly.
        int headerWidth = lossless ? width : paddedWidth;
        int headerHeight = lossless ? height : paddedHeight;

        byte[] EncodeTileTrial(bool trialScreenContentTools, bool trialIntrabc) => Av1TileEncoder.EncodeTile(
            yPlane, paddedWidth, paddedHeight,
            uPlane, vPlane, paddedChromaWidth, paddedChromaHeight,
            reconY, reconU, reconV,
            monoChrome, baseQIdx, lossless, chroma444, effort, trialScreenContentTools, trialIntrabc,
            trueWidth: headerWidth, trueHeight: headerHeight);

        void EncodeTileTrialWithHook(bool trialScreenContentTools, bool trialIntrabc, Action<Av1BlockDecisionRecord> hook) => Av1TileEncoder.EncodeTile(
            yPlane, paddedWidth, paddedHeight,
            uPlane, vPlane, paddedChromaWidth, paddedChromaHeight,
            reconY, reconU, reconV,
            monoChrome, baseQIdx, lossless, chroma444, effort, trialScreenContentTools, trialIntrabc,
            trueWidth: headerWidth, trueHeight: headerHeight, onLeafCommitted: hook);

        bool allowScreenContentTools = heuristicScreenContentTools;
        bool allowIntrabc = heuristicIntrabc;

        // Multi-tile (tile rows only, lossy only): each tile row is encoded as an independent image slice, which
        // is exactly what AV1 requires at a tile boundary -- no above neighbors, fresh CDFs, fresh coefficient
        // contexts -- so tiles can be encoded in parallel. The slices are written back into the shared
        // reconstruction planes the loop-filter/CDEF searches below run over.
        int chosenTileRowsLog2 = lossless || onLeafCommitted is not null
            ? 0
            : ResolveTileRowsLog2(tileRowsLog2, paddedHeight);
        int tileSizeBytes = 1;
        byte[] tileBytes;
        if (chosenTileRowsLog2 > 0)
        {
            tileBytes = EncodeTileRows(
                yPlane, uPlane, vPlane, reconY, reconU, reconV,
                paddedWidth, paddedHeight, paddedChromaWidth, paddedChromaHeight,
                monoChrome, baseQIdx, effort, chosenTileRowsLog2, maxDegreeOfParallelism, out tileSizeBytes);
        }
        else
        {
            tileBytes = EncodeTileTrial(allowScreenContentTools, allowIntrabc);
        }

        // The real trial itself: the heuristic above only gates whether this ever runs at all (an image the
        // heuristic already ruled out as screen-content-like never pays this extra encode cost) -- once it
        // says yes, don't just trust it. Try genuinely turning the tools back off (and, separately, IntraBC
        // off while keeping palette on) and keep whichever REAL committed byte count is actually smaller.
        // reconY/U/V are safe to reuse across trials: every candidate here is lossless-only, and lossless
        // never reads them again after EncodeTile returns (deblocking/CDEF below are non-lossless-only), so
        // each trial's full reconstruction simply overwrites whatever the previous one left behind.
        if (lossless && heuristicScreenContentTools)
        {
            byte[] withoutToolsBytes = EncodeTileTrial(false, false);
            if (withoutToolsBytes.Length < tileBytes.Length)
            {
                tileBytes = withoutToolsBytes;
                allowScreenContentTools = false;
                allowIntrabc = false;
            }

            if (heuristicIntrabc)
            {
                byte[] withoutIntrabcBytes = EncodeTileTrial(true, false);
                if (withoutIntrabcBytes.Length < tileBytes.Length)
                {
                    tileBytes = withoutIntrabcBytes;
                    allowScreenContentTools = true;
                    allowIntrabc = false;
                }
            }
        }

        // Diagnostic-only (project plan's Phase 1/Step 6 structural decision-log tool): a real, deterministic
        // re-encode using the now-finalized allowScreenContentTools/allowIntrabc, purely to drive
        // onLeafCommitted -- tileBytes itself (the real output) already came from the trial process above and
        // is never replaced by this. Reusing reconY/U/V here is exactly as safe as the trial process's own
        // reuse just above (see its remarks): this call fully overwrites every pixel it needs before reading
        // any of them back. Never runs when onLeafCommitted is null (every production call site), so this
        // adds zero cost/risk outside the harness that actually passes a hook.
        if (onLeafCommitted is not null)
        {
            EncodeTileTrialWithHook(allowScreenContentTools, allowIntrabc, onLeafCommitted);
        }

        // Deblocking (spec §7.14) is a lossy-only tool -- codedLossless's own short-circuit means
        // loop_filter_params() never even reaches the bitstream at lossless (see Av1FrameHeaderWriter.Write's
        // own lossless remarks), so there's nothing to search for there. Chooses and applies the filter to
        // reconY/U/V in place (see Av1InLoopFilterSearch.SearchAndApply's remarks) *before* the frame header
        // is written, so the header can signal the real, chosen level -- Av1TileEncoder.EncodeTile already
        // finished producing every pixel these buffers will ever hold, so nothing about the tile's own
        // (already-flushed) bitstream depends on this running afterward.
        int loopFilterLevel = 0;
        var cdefChoice = Av1CdefChoice.Off;
        if (!lossless)
        {
            loopFilterLevel = Av1InLoopFilterSearch.SearchAndApply(
                reconY, reconU, reconV,
                yPlane, uPlane, vPlane,
                paddedWidth, paddedHeight, paddedChromaWidth, paddedChromaHeight,
                monoChrome, baseQIdx);

            // CDEF (spec §7.15) runs after deblocking, per spec's own filter ordering (Av1FrameDecoder.
            // DecodeTileGroup applies them in exactly this order) -- reconY/U/V already reflect the chosen
            // deblocking level at this point, so Av1CdefSearch starts from that, not the pre-deblock
            // reconstruction. Updates reconY/U/V in place with the winning candidate's content (see
            // Av1CdefSearch.SearchAndApply's own buffer-ownership remarks for why it copies rather than
            // reassigning these arrays).
            cdefChoice = Av1CdefSearch.SearchAndApply(
                reconY, reconU, reconV,
                yPlane, uPlane, vPlane,
                paddedWidth, paddedHeight, paddedChromaWidth, paddedChromaHeight,
                monoChrome, baseQIdx, loopFilterLevel);
        }

        byte[] seqHeaderPayload = Av1SequenceHeaderWriter.Write(headerWidth, headerHeight, monoChrome, chroma444, enableCdef: !lossless, use128x128Superblock: lossless, enableRestoration: lossless);

        var frameHeaderWriter = new Av1BitWriter();
        Av1FrameHeaderWriter.Write(frameHeaderWriter, headerWidth, headerHeight, monoChrome, baseQIdx, lossless, loopFilterLevel, enableCdef: !lossless, cdefChoice, allowScreenContentTools, allowIntrabc, reducedTxSet: !lossless, chosenTileRowsLog2, tileSizeBytes);
        byte[] frameHeaderPayload = frameHeaderWriter.ToArray();

        // A single combined OBU_FRAME (spec's frame_obu(): frame_header_obu() + byte_alignment() +
        // tile_group_obu(sz), all in one OBU payload) rather than separate FrameHeader/TileGroup OBUs --
        // matches what real encoders (confirmed against aomenc's own --obu output) actually emit for a
        // single-tile-group frame, saving one OBU header+size field's worth of bytes. frameHeaderPayload is
        // already byte-aligned (Av1BitWriter.ToArray() rounds up to a whole byte with the same zero-padding
        // ByteAlign() would write), so it can be concatenated directly with tileBytes with no extra
        // alignment step. Av1FrameDecoder.Decode already parses this OBU_FRAME form (needed for real-world
        // AVIF interop regardless of what this encoder itself used to emit), so this needs no decoder change.
        byte[] frameObuPayload = new byte[frameHeaderPayload.Length + tileBytes.Length];
        Array.Copy(frameHeaderPayload, frameObuPayload, frameHeaderPayload.Length);
        Array.Copy(tileBytes, 0, frameObuPayload, frameHeaderPayload.Length, tileBytes.Length);

        var output = new List<byte>();
        Av1ObuWriter.WriteObu(output, Decoding.Av1.Av1ObuType.TemporalDelimiter, ReadOnlySpan<byte>.Empty);
        Av1ObuWriter.WriteObu(output, Decoding.Av1.Av1ObuType.SequenceHeader, seqHeaderPayload);
        Av1ObuWriter.WriteObu(output, Decoding.Av1.Av1ObuType.Frame, frameObuPayload);

        int seqLevelIdx = Av1SequenceHeaderWriter.ComputeSeqLevelIdx(headerWidth, headerHeight);
        return new Av1EncodedFrame(output.ToArray(), width, height, monoChrome, chroma444, seqLevelIdx);
    }

    private const int MinTileRowSuperblocks = 4;
    private const int MaxAutoTileRowsLog2 = 3;

    /// <summary>
    /// The <c>tile_rows_log2</c> to encode with. An explicit request is clamped to what the frame can signal; automatic (null)
    /// picks the most tile rows that still leave each tile at least <see cref="MinTileRowSuperblocks"/> superblocks tall
    /// (so small images stay single-tile), capped at <see cref="MaxAutoTileRowsLog2"/>. Depends only on the image size,
    /// never on the core count of the machine, so the same input always produces the same bytes.
    /// </summary>
    private static int ResolveTileRowsLog2(int? requested, int paddedHeight)
    {
        int miRows = paddedHeight / 4;
        int maxLog2 = Av1TileInfoWriter.MaxTileRowsLog2(miRows, use128x128Superblock: false);
        if (requested is int explicitLog2)
        {
            return Math.Clamp(explicitLog2, 0, maxLog2);
        }

        int sbRows = paddedHeight / 64;
        int log2 = 0;
        while (log2 < MaxAutoTileRowsLog2 && log2 < maxLog2 && (sbRows >> (log2 + 1)) >= MinTileRowSuperblocks)
        {
            log2++;
        }

        return log2;
    }

    /// <summary>
    /// Encodes the frame as uniformly spaced tile rows in parallel and returns the tile group payload
    /// (<c>tile_start_and_end_present_flag</c> byte, then each non-final tile prefixed by its little-endian size-minus-1).
    /// </summary>
    private static byte[] EncodeTileRows(
        int[] yPlane, int[]? uPlane, int[]? vPlane,
        int[] reconY, int[]? reconU, int[]? reconV,
        int paddedWidth, int paddedHeight, int paddedChromaWidth, int paddedChromaHeight,
        bool monoChrome, int baseQIdx, int effort, int tileRowsLog2, int maxDegreeOfParallelism, out int tileSizeBytes)
    {
        int[] rowStarts = Av1TileInfoWriter.ComputeRowStarts(paddedHeight / 4, use128x128Superblock: false, tileRowsLog2);
        int tileCount = rowStarts.Length - 1;
        var tiles = new byte[tileCount][];

        Parallel.For(0, tileCount, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, tile =>
        {
            int y0 = rowStarts[tile] * 4;
            int height = (rowStarts[tile + 1] * 4) - y0;
            int lumaLen = paddedWidth * height;

            int[] ySlice = yPlane.AsSpan(y0 * paddedWidth, lumaLen).ToArray();
            int[] reconYSlice = new int[lumaLen];
            int[]? uSlice = null, vSlice = null, reconUSlice = null, reconVSlice = null;
            int chromaHeight = 0;
            int chromaOffset = (y0 / 2) * paddedChromaWidth;
            if (!monoChrome)
            {
                chromaHeight = height / 2;
                int chromaLen = paddedChromaWidth * chromaHeight;
                uSlice = uPlane.AsSpan(chromaOffset, chromaLen).ToArray();
                vSlice = vPlane.AsSpan(chromaOffset, chromaLen).ToArray();
                reconUSlice = new int[chromaLen];
                reconVSlice = new int[chromaLen];
            }

            tiles[tile] = Av1TileEncoder.EncodeTile(
                ySlice, paddedWidth, height,
                uSlice, vSlice, paddedChromaWidth, chromaHeight,
                reconYSlice, reconUSlice, reconVSlice,
                monoChrome, baseQIdx, lossless: false, chroma444: false, effort: effort, allowScreenContentTools: false, allowIntrabc: false,
                trueWidth: paddedWidth, trueHeight: height);

            reconYSlice.CopyTo(reconY.AsSpan(y0 * paddedWidth));
            if (!monoChrome)
            {
                reconUSlice.CopyTo(reconU.AsSpan(chromaOffset));
                reconVSlice.CopyTo(reconV.AsSpan(chromaOffset));
            }
        });

        int maxSizeMinus1 = 0;
        for (int i = 0; i < tileCount - 1; i++)
        {
            maxSizeMinus1 = Math.Max(maxSizeMinus1, tiles[i].Length - 1);
        }

        tileSizeBytes = maxSizeMinus1 <= 0xFF ? 1 : maxSizeMinus1 <= 0xFFFF ? 2 : maxSizeMinus1 <= 0xFFFFFF ? 3 : 4;

        int total = 1 + tiles[tileCount - 1].Length;
        for (int i = 0; i < tileCount - 1; i++)
        {
            total += tileSizeBytes + tiles[i].Length;
        }

        var group = new byte[total];
        int cursor = 1; // group[0] == 0: tile_start_and_end_present_flag = 0, then byte_alignment().
        for (int i = 0; i < tileCount; i++)
        {
            if (i < tileCount - 1)
            {
                int sizeMinus1 = tiles[i].Length - 1;
                for (int b = 0; b < tileSizeBytes; b++)
                {
                    group[cursor++] = (byte)(sizeMinus1 >> (8 * b));
                }
            }

            tiles[i].CopyTo(group, cursor);
            cursor += tiles[i].Length;
        }

        return group;
    }

    /// <summary>Pads a <paramref name="srcWidth"/> x <paramref name="srcHeight"/> plane up to <paramref name="paddedWidth"/> x <paramref name="paddedHeight"/> by replicating the last real row/column into the padding region.</summary>
    private static int[] PadPlane(int[] src, int srcWidth, int srcHeight, int paddedWidth, int paddedHeight)
    {
        if (srcWidth == paddedWidth && srcHeight == paddedHeight)
        {
            return src;
        }

        var padded = new int[paddedWidth * paddedHeight];
        for (int row = 0; row < paddedHeight; row++)
        {
            int srcRow = Math.Min(row, srcHeight - 1);
            int srcRowBase = srcRow * srcWidth;
            int destRowBase = row * paddedWidth;
            for (int col = 0; col < paddedWidth; col++)
            {
                int srcCol = Math.Min(col, srcWidth - 1);
                padded[destRowBase + col] = src[srcRowBase + srcCol];
            }
        }

        return padded;
    }
}
