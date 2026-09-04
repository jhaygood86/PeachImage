namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Top-level AV1 encode entry point: converts a source image to YUV, pads it to a 64-pixel-multiple coded
/// canvas (edge-replicated -- see <see cref="Av1TileEncoder"/>'s remarks on why this is required for its
/// simplified, edge-case-free superblock traversal), writes the sequence/frame headers and the single tile,
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
    public static Av1EncodedFrame Encode(ReadOnlySpan<byte> pixels, int width, int height, bool monoChrome, int quality, bool lossless = false)
    {
        int paddedWidth = ((width + 63) / 64) * 64;
        int paddedHeight = ((height + 63) / 64) * 64;

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

        var reconY = new int[paddedWidth * paddedHeight];
        int[]? reconU = monoChrome ? null : new int[paddedChromaWidth * paddedChromaHeight];
        int[]? reconV = monoChrome ? null : new int[paddedChromaWidth * paddedChromaHeight];

        byte[] tileBytes = Av1TileEncoder.EncodeTile(
            yPlane, paddedWidth, paddedHeight,
            uPlane, vPlane, paddedChromaWidth, paddedChromaHeight,
            reconY, reconU, reconV,
            monoChrome, baseQIdx, lossless, chroma444);

        // Deblocking (spec §7.14) is a lossy-only tool -- codedLossless's own short-circuit means
        // loop_filter_params() never even reaches the bitstream at lossless (see Av1FrameHeaderWriter.Write's
        // own lossless remarks), so there's nothing to search for there. Chooses and applies the filter to
        // reconY/U/V in place (see Av1InLoopFilterSearch.SearchAndApply's remarks) *before* the frame header
        // is written, so the header can signal the real, chosen level -- Av1TileEncoder.EncodeTile already
        // finished producing every pixel these buffers will ever hold, so nothing about the tile's own
        // (already-flushed) bitstream depends on this running afterward.
        int loopFilterLevel = 0;
        if (!lossless)
        {
            loopFilterLevel = Av1InLoopFilterSearch.SearchAndApply(
                reconY, reconU, reconV,
                yPlane, uPlane, vPlane,
                paddedWidth, paddedHeight, paddedChromaWidth, paddedChromaHeight,
                monoChrome, baseQIdx);
        }

        byte[] seqHeaderPayload = Av1SequenceHeaderWriter.Write(paddedWidth, paddedHeight, monoChrome, chroma444);

        var frameHeaderWriter = new Av1BitWriter();
        Av1FrameHeaderWriter.Write(frameHeaderWriter, paddedWidth, paddedHeight, monoChrome, baseQIdx, lossless, loopFilterLevel);
        byte[] frameHeaderPayload = frameHeaderWriter.ToArray();

        var output = new List<byte>();
        Av1ObuWriter.WriteObu(output, Decoding.Av1.Av1ObuType.TemporalDelimiter, ReadOnlySpan<byte>.Empty);
        Av1ObuWriter.WriteObu(output, Decoding.Av1.Av1ObuType.SequenceHeader, seqHeaderPayload);
        Av1ObuWriter.WriteObu(output, Decoding.Av1.Av1ObuType.FrameHeader, frameHeaderPayload);
        Av1ObuWriter.WriteObu(output, Decoding.Av1.Av1ObuType.TileGroup, tileBytes);

        return new Av1EncodedFrame(output.ToArray(), width, height, monoChrome, chroma444);
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
