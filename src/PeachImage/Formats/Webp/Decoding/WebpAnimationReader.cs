using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding;

/// <summary>
/// Reads an animated WebP file's <c>ANIM</c> header and, lazily, its <c>ANMF</c> frames. The animation-aware
/// sibling of <see cref="WebpContainerReader"/>: <see cref="ReadHeader"/> continues from a stream already
/// positioned right after VP8X (<see cref="WebpContainerPrelude.HasAnimation"/> already confirmed true by the
/// caller), reading up to and including the required <c>ANIM</c> chunk; <see cref="ReadFrames"/> then lazily
/// decodes and composites one <c>ANMF</c> chunk at a time as the caller enumerates.
/// </summary>
internal static class WebpAnimationReader
{
    /// <summary>
    /// Reads any <c>ICCP</c>/<c>EXIF</c>/<c>XMP </c> chunks encountered before <c>ANIM</c> into
    /// <paramref name="metadata"/>, then the required <c>ANIM</c> chunk itself. Throws if <c>ANIM</c> never
    /// appears, or if an <c>ANMF</c> chunk is encountered first — both are structurally mandatory/ordered for
    /// an animated file, unlike GIF's lenient/optional Graphic Control Extension.
    /// </summary>
    public static WebpAnimationHeader ReadHeader(Stream stream, ImageMetadata metadata, WebpContainerPrelude prelude)
    {
        while (WebpChunkReader.TryReadNext(stream, out var header))
        {
            switch (header.FourCc)
            {
                case "ANIM":
                    var animData = WebpChunkReader.ReadPayload(stream, header.Size);
                    int loopCount = ParseAnim(animData);
                    return new WebpAnimationHeader(prelude.CanvasWidth!.Value, prelude.CanvasHeight!.Value, loopCount);

                case "ANMF":
                    throw new WebpDecodingException("WebP file's VP8X header declares animation but no ANIM chunk was found before the first ANMF chunk.");

                case "ICCP":
                    WebpMetadataReader.AddIcc(metadata, WebpChunkReader.ReadPayload(stream, header.Size));
                    break;

                case "EXIF":
                    WebpMetadataReader.AddExif(metadata, WebpChunkReader.ReadPayload(stream, header.Size));
                    break;

                case "XMP ":
                    WebpMetadataReader.AddXmp(metadata, WebpChunkReader.ReadPayload(stream, header.Size));
                    break;

                default:
                    WebpChunkReader.SkipPayload(stream, header.Size);
                    break;
            }
        }

        throw new WebpDecodingException("WebP file's VP8X header declares animation but no ANIM chunk was found.");
    }

    /// <summary>
    /// Lazily yields one <see cref="AnimatedImageFrame"/> per <c>ANMF</c> chunk, decoding+compositing on
    /// demand. Stray <c>ICCP</c>/<c>EXIF</c>/<c>XMP </c> chunks between/after ANMF chunks are tolerated
    /// (skipped into <paramref name="metadata"/>), matching this codebase's existing ancillary-chunk leniency.
    /// Malformed structural data (short ANMF header, missing image sub-chunk, rect exceeding the canvas)
    /// always throws <see cref="WebpDecodingException"/> — unlike GIF's truncated-GCE leniency, nothing here
    /// is optional per spec.
    /// </summary>
    public static IEnumerable<AnimatedImageFrame> ReadFrames(Stream stream, ImageMetadata metadata, WebpAnimationHeader header)
    {
        var compositor = new WebpFrameCompositor(header.CanvasWidth, header.CanvasHeight);
        int frameCount = 0;

        while (frameCount < WebpDecodingLimits.MaxFrameCount && WebpChunkReader.TryReadNext(stream, out var chunkHeader))
        {
            switch (chunkHeader.FourCc)
            {
                case "ANMF":
                    frameCount++;
                    yield return ReadFrame(stream, chunkHeader.Size, header, compositor);
                    break;

                case "ICCP":
                    WebpMetadataReader.AddIcc(metadata, WebpChunkReader.ReadPayload(stream, chunkHeader.Size));
                    break;

                case "EXIF":
                    WebpMetadataReader.AddExif(metadata, WebpChunkReader.ReadPayload(stream, chunkHeader.Size));
                    break;

                case "XMP ":
                    WebpMetadataReader.AddXmp(metadata, WebpChunkReader.ReadPayload(stream, chunkHeader.Size));
                    break;

                default:
                    WebpChunkReader.SkipPayload(stream, chunkHeader.Size);
                    break;
            }
        }
    }

    private static AnimatedImageFrame ReadFrame(Stream stream, uint chunkSize, WebpAnimationHeader header, WebpFrameCompositor compositor)
    {
        if (chunkSize < 16)
        {
            throw new WebpDecodingException($"ANMF chunk header must be at least 16 bytes, was {chunkSize}.");
        }

        Span<byte> frameHeaderBytes = stackalloc byte[16];
        WebpStreamHelpers.ReadExactlyOrThrow(stream, frameHeaderBytes);
        var chunk = ParseFrameHeader(frameHeaderBytes);

        if (chunk.X + chunk.Width > header.CanvasWidth || chunk.Y + chunk.Height > header.CanvasHeight)
        {
            throw new WebpDecodingException($"ANMF frame rectangle ({chunk.X},{chunk.Y},{chunk.Width}x{chunk.Height}) exceeds the {header.CanvasWidth}x{header.CanvasHeight} canvas declared by VP8X.");
        }

        uint frameDataSize = chunkSize - 16;
        var (format, bitstreamData, alphaData) = ReadFrameData(stream, frameDataSize);

        var decoded = WebpBitstreamDecoder.Decode(format, bitstreamData, alphaData);
        if (decoded.Width != chunk.Width || decoded.Height != chunk.Height)
        {
            throw new WebpDecodingException($"ANMF frame's decoded size {decoded.Width}x{decoded.Height} does not match its declared size {chunk.Width}x{chunk.Height}.");
        }

        // decoded/rgba are both owned, pool-rented Images local to this one frame -- DrawFrame copies rgba's
        // bytes into the compositor's own persistent canvas (never aliases them), so both must be disposed
        // here rather than left for the caller, which never sees either. This runs once per ANMF chunk, so
        // leaving these undisposed would leak a rented buffer per animation frame, not just per file.
        var rgba = PixelFormatConverter.ConvertIfNeeded(decoded, PixelFormat.Rgba32);
        if (!ReferenceEquals(rgba, decoded))
        {
            decoded.Dispose();
        }

        var composited = compositor.DrawFrame((chunk.X, chunk.Y, chunk.Width, chunk.Height), rgba.GetPixelSpan(), chunk.Blend, chunk.DisposeToBackground);
        rgba.Dispose();
        return new AnimatedImageFrame(composited, chunk.Duration, chunk.DisposeToBackground ? FrameDisposalMethod.RestoreToBackground : FrameDisposalMethod.DoNotDispose);
    }

    private static WebpFrameChunk ParseFrameHeader(ReadOnlySpan<byte> data)
    {
        int x = 2 * (data[0] | (data[1] << 8) | (data[2] << 16));
        int y = 2 * (data[3] | (data[4] << 8) | (data[5] << 16));
        int width = 1 + (data[6] | (data[7] << 8) | (data[8] << 16));
        int height = 1 + (data[9] | (data[10] << 8) | (data[11] << 16));
        int durationMs = data[12] | (data[13] << 8) | (data[14] << 16);
        byte flags = data[15];

        // bit 0 (0x01): dispose to background before the next frame draws. bit 1 (0x02): do not blend
        // (overwrite outright) rather than alpha source-over. Bits 2-7 are reserved.
        bool disposeToBackground = (flags & 0x01) != 0;
        bool blend = (flags & 0x02) == 0;

        return new WebpFrameChunk(x, y, width, height, TimeSpan.FromMilliseconds(durationMs), disposeToBackground, blend);
    }

    /// <summary>
    /// Reads an ANMF chunk's Frame Data — the chunk's bytes after its fixed 16-byte header — as a bounded
    /// nested chunk run: an optional <c>ALPH</c> + <c>VP8 </c>, or a bare <c>VP8L</c> (never both ALPH+VP8L,
    /// never a nested VP8X), reusing the same <see cref="WebpChunkReader"/> primitives the top-level container
    /// uses, tracked against a running byte budget instead of a dedicated stream-slicing type.
    /// </summary>
    private static (WebpBitstreamFormat Format, byte[] BitstreamData, byte[]? AlphaData) ReadFrameData(Stream stream, uint frameDataSize)
    {
        long remaining = frameDataSize;
        WebpBitstreamFormat? format = null;
        byte[]? bitstreamData = null;
        byte[]? alphaData = null;

        while (remaining > 0)
        {
            if (!WebpChunkReader.TryReadNext(stream, out var header))
            {
                throw new WebpDecodingException("ANMF frame data contains neither a VP8 nor VP8L image chunk.");
            }

            long chunkCost = 8L + header.Size + (header.Size % 2);
            if (chunkCost > remaining)
            {
                throw new WebpDecodingException("ANMF frame data's declared sub-chunk size exceeds the ANMF chunk's own size.");
            }

            remaining -= chunkCost;

            switch (header.FourCc)
            {
                case "VP8 ":
                    if (format is not null)
                    {
                        throw new WebpDecodingException("ANMF frame data contains more than one VP8/VP8L image chunk.");
                    }

                    format = WebpBitstreamFormat.Lossy;
                    bitstreamData = WebpChunkReader.ReadPayload(stream, header.Size);
                    break;

                case "VP8L":
                    if (format is not null)
                    {
                        throw new WebpDecodingException("ANMF frame data contains more than one VP8/VP8L image chunk.");
                    }

                    format = WebpBitstreamFormat.Lossless;
                    bitstreamData = WebpChunkReader.ReadPayload(stream, header.Size);
                    break;

                case "ALPH":
                    alphaData = WebpChunkReader.ReadPayload(stream, header.Size);
                    break;

                default:
                    WebpChunkReader.SkipPayload(stream, header.Size);
                    break;
            }
        }

        if (format is null || bitstreamData is null)
        {
            throw new WebpDecodingException("ANMF frame data contains neither a VP8 nor VP8L image chunk.");
        }

        return (format.Value, bitstreamData, format == WebpBitstreamFormat.Lossy ? alphaData : null);
    }

    private static int ParseAnim(byte[] data)
    {
        if (data.Length < 6)
        {
            throw new WebpDecodingException($"ANIM chunk must be at least 6 bytes, was {data.Length}.");
        }

        // bytes[0..4) are the background color (BGRA), intentionally unused — see WebpFrameCompositor's doc
        // comment for why the canvas starts fully transparent instead.
        return data[4] | (data[5] << 8);
    }
}
