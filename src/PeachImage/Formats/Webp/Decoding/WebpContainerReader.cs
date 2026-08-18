using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding;

/// <summary>The VP8X chunk's flags/canvas info, or the all-<see langword="false"/>/<see langword="null"/> default for a "simple"-format file with no VP8X chunk.</summary>
internal readonly record struct WebpContainerPrelude(bool HasAlpha, bool HasAnimation, int? CanvasWidth, int? CanvasHeight);

/// <summary>
/// Parses a WebP file's RIFF container (the "simple" format — a bare <c>VP8 </c>/<c>VP8L</c> chunk directly
/// after the <c>WEBP</c> form type — and the "extended" format, gated by a <c>VP8X</c> chunk that also allows
/// <c>ALPH</c>/<c>ICCP</c>/<c>EXIF</c>/<c>XMP </c>), dispatching every chunk generically by its FourCC rather
/// than assuming a fixed chunk order (real-world encoders aren't always spec-perfect about ordering, and
/// libwebp's own demuxer parses generically too).
/// </summary>
internal static class WebpContainerReader
{
    /// <summary>
    /// Reads "RIFF"+size+"WEBP", then the VP8X chunk if the very next chunk is VP8X (the spec-conformant,
    /// overwhelmingly common layout). Shared by <see cref="Read(Stream, ImageMetadata)"/>, <see cref="WebpDecoder.Decode"/>,
    /// <see cref="WebpDecoder.DecodeAnimation"/>, and <see cref="WebpDecoder.Identify"/> so the RIFF
    /// header/VP8X chunk are only ever parsed once per call, then branched on — no peek-and-rewind or
    /// stream-wrapping trick needed, since the stream is only ever read forward. If the next chunk is
    /// something other than VP8X (a "simple"-format file, or a non-conformant file with VP8X not first), its
    /// already-read 8-byte header is handed back via <paramref name="pendingHeader"/> instead of being
    /// discarded, so the caller's own chunk loop can process it as its first iteration.
    /// </summary>
    internal static WebpContainerPrelude ReadPrelude(Stream stream, out WebpChunkHeader? pendingHeader)
    {
        ReadRiffHeader(stream);

        if (!WebpChunkReader.TryReadNext(stream, out var header))
        {
            pendingHeader = null;
            return default;
        }

        if (header.FourCc != "VP8X")
        {
            pendingHeader = header;
            return default;
        }

        pendingHeader = null;
        var vp8XData = WebpChunkReader.ReadPayload(stream, header.Size);
        ParseVp8X(vp8XData, out bool hasAlpha, out bool hasAnimation, out int? canvasWidth, out int? canvasHeight);
        return new WebpContainerPrelude(hasAlpha, hasAnimation, canvasWidth, canvasHeight);
    }

    /// <summary>Reads a non-animated WebP file's RIFF/WEBP container from <paramref name="stream"/>, collecting metadata chunks into <paramref name="metadata"/> as they're encountered.</summary>
    public static WebpContainerInfo Read(Stream stream, ImageMetadata metadata)
    {
        var prelude = ReadPrelude(stream, out var pendingHeader);
        return Read(stream, metadata, prelude, pendingHeader);
    }

    /// <summary>
    /// Continues a non-animated read from an already-parsed <paramref name="prelude"/>/<paramref name="pendingHeader"/>
    /// (i.e. an already-consumed <see cref="ReadPrelude"/> call) — used by <see cref="WebpDecoder.Decode"/> and
    /// <see cref="WebpDecoder.Identify"/> so they can branch on <see cref="WebpContainerPrelude.HasAnimation"/>
    /// themselves without this method re-reading the RIFF header/VP8X chunk a second time.
    /// </summary>
    internal static WebpContainerInfo Read(Stream stream, ImageMetadata metadata, WebpContainerPrelude prelude, WebpChunkHeader? pendingHeader)
    {
        if (prelude.HasAnimation)
        {
            throw AnimatedFileException();
        }

        bool hasAlphaFlag = prelude.HasAlpha;
        int? canvasWidth = prelude.CanvasWidth;
        int? canvasHeight = prelude.CanvasHeight;
        bool sawAnimationChunk = false;
        WebpBitstreamFormat? format = null;
        byte[]? imageBytes = null;
        byte[]? alphaBytes = null;

        WebpChunkHeader? nextHeader = pendingHeader;
        while (true)
        {
            WebpChunkHeader chunkHeader;
            if (nextHeader is { } pending)
            {
                chunkHeader = pending;
                nextHeader = null;
            }
            else if (!WebpChunkReader.TryReadNext(stream, out chunkHeader))
            {
                break;
            }

            switch (chunkHeader.FourCc)
            {
                case "VP8X":
                    // A second/non-leading VP8X chunk is non-conformant but tolerated (last one wins),
                    // matching this reader's general "strict about structural data, lenient about ordering"
                    // posture — ReadPrelude already handled the common case where VP8X is the first chunk.
                    var vp8XData = WebpChunkReader.ReadPayload(stream, chunkHeader.Size);
                    ParseVp8X(vp8XData, out hasAlphaFlag, out bool hasAnimation, out canvasWidth, out canvasHeight);
                    if (hasAnimation)
                    {
                        throw AnimatedFileException();
                    }

                    break;

                case "VP8 ":
                    RequireNoImageChunkYet(format);
                    format = WebpBitstreamFormat.Lossy;
                    imageBytes = WebpChunkReader.ReadPayload(stream, chunkHeader.Size);
                    break;

                case "VP8L":
                    RequireNoImageChunkYet(format);
                    format = WebpBitstreamFormat.Lossless;
                    imageBytes = WebpChunkReader.ReadPayload(stream, chunkHeader.Size);
                    break;

                case "ALPH":
                    alphaBytes = WebpChunkReader.ReadPayload(stream, chunkHeader.Size);
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

                case "ANIM":
                case "ANMF":
                    sawAnimationChunk = true;
                    WebpChunkReader.SkipPayload(stream, chunkHeader.Size);
                    break;

                default:
                    // Unrecognized chunk (e.g. a future extension) — skip, matching PNG's ancillary-chunk leniency.
                    WebpChunkReader.SkipPayload(stream, chunkHeader.Size);
                    break;
            }
        }

        if (sawAnimationChunk)
        {
            throw AnimatedFileException();
        }

        if (format is null || imageBytes is null)
        {
            throw new WebpDecodingException("WebP file does not contain a VP8 or VP8L image chunk.");
        }

        bool isExtended = canvasWidth is not null;
        if (isExtended && format == WebpBitstreamFormat.Lossy && hasAlphaFlag && alphaBytes is null)
        {
            throw new WebpDecodingException("WebP file's VP8X header declares alpha but no ALPH chunk was found.");
        }

        // A stray ALPH chunk alongside a VP8L image chunk is non-conformant (VP8L carries its own alpha via
        // its alpha_is_used header bit) but harmless — ignore it rather than treating it as a hard error,
        // consistent with this codebase's general "strict about structural data, lenient about redundant or
        // inconsistent ancillary data" posture (see PNG's ancillary chunk handling).
        byte[]? effectiveAlpha = format == WebpBitstreamFormat.Lossy ? alphaBytes : null;

        return new WebpContainerInfo
        {
            Format = format.Value,
            BitstreamData = imageBytes,
            AlphaData = effectiveAlpha,
            CanvasWidth = canvasWidth,
            CanvasHeight = canvasHeight,
        };
    }

    private static WebpDecodingException AnimatedFileException() =>
        new("WebP file is animated; use WebpDecoder.DecodeAnimation (or AnimatedImage.Load) instead of the single-image decode path.");

    private static void RequireNoImageChunkYet(WebpBitstreamFormat? format)
    {
        if (format is not null)
        {
            throw new WebpDecodingException("WebP file contains more than one VP8/VP8L image chunk.");
        }
    }

    private static void ReadRiffHeader(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[12];
        WebpStreamHelpers.ReadExactlyOrThrow(stream, buffer);

        if (buffer[0] != (byte)'R' || buffer[1] != (byte)'I' || buffer[2] != (byte)'F' || buffer[3] != (byte)'F')
        {
            throw new WebpDecodingException("Stream does not start with a RIFF header.");
        }

        // buffer[4..8] is the RIFF form's declared total size (little-endian). Intentionally not hard-validated
        // against the real stream length — real-world encoders occasionally get this field slightly wrong, and
        // libwebp itself tolerates it, so only chunk-level structure is treated as authoritative here.
        if (buffer[8] != (byte)'W' || buffer[9] != (byte)'E' || buffer[10] != (byte)'B' || buffer[11] != (byte)'P')
        {
            throw new WebpDecodingException("Stream is not a WebP file (missing 'WEBP' RIFF form type).");
        }
    }

    private static void ParseVp8X(byte[] data, out bool hasAlpha, out bool hasAnimation, out int? canvasWidth, out int? canvasHeight)
    {
        if (data.Length != 10)
        {
            throw new WebpDecodingException($"VP8X chunk must be exactly 10 bytes, was {data.Length}.");
        }

        byte flags = data[0];
        hasAlpha = (flags & Vp8XFlags.AlphaBit) != 0;
        hasAnimation = (flags & Vp8XFlags.AnimationBit) != 0;

        // bytes[1..4) are reserved and MUST be 0 per spec, but real encoders occasionally leave stray bits set —
        // tolerated here rather than hard-failing, matching libwebp's own forward-compatible stance.
        int width = 1 + (data[4] | (data[5] << 8) | (data[6] << 16));
        int height = 1 + (data[7] | (data[8] << 8) | (data[9] << 16));

        if (width > WebpDecodingLimits.MaxDimension || height > WebpDecodingLimits.MaxDimension)
        {
            throw new WebpDecodingException($"WebP canvas {width}x{height} exceeds the maximum supported dimension of {WebpDecodingLimits.MaxDimension}.");
        }

        canvasWidth = width;
        canvasHeight = height;
    }
}
