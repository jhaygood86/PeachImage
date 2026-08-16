using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding;

/// <summary>
/// Parses a WebP file's RIFF container (the "simple" format — a bare <c>VP8 </c>/<c>VP8L</c> chunk directly
/// after the <c>WEBP</c> form type — and the "extended" format, gated by a <c>VP8X</c> chunk that also allows
/// <c>ALPH</c>/<c>ICCP</c>/<c>EXIF</c>/<c>XMP </c>), dispatching every chunk generically by its FourCC rather
/// than assuming a fixed chunk order (real-world encoders aren't always spec-perfect about ordering, and
/// libwebp's own demuxer parses generically too).
/// </summary>
internal static class WebpContainerReader
{
    /// <summary>Reads the RIFF/WEBP container from <paramref name="stream"/>, collecting metadata chunks into <paramref name="metadata"/> as they're encountered.</summary>
    public static WebpContainerInfo Read(Stream stream, ImageMetadata metadata)
    {
        ReadRiffHeader(stream);

        int? canvasWidth = null;
        int? canvasHeight = null;
        bool hasAlphaFlag = false;
        bool hasAnimationFlag = false;
        bool sawAnimationChunk = false;
        WebpBitstreamFormat? format = null;
        byte[]? imageBytes = null;
        byte[]? alphaBytes = null;

        while (WebpChunkReader.TryReadNext(stream, out var header))
        {
            switch (header.FourCc)
            {
                case "VP8X":
                    var vp8XData = WebpChunkReader.ReadPayload(stream, header.Size);
                    ParseVp8X(vp8XData, out hasAlphaFlag, out hasAnimationFlag, out canvasWidth, out canvasHeight);
                    break;

                case "VP8 ":
                    RequireNoImageChunkYet(format);
                    format = WebpBitstreamFormat.Lossy;
                    imageBytes = WebpChunkReader.ReadPayload(stream, header.Size);
                    break;

                case "VP8L":
                    RequireNoImageChunkYet(format);
                    format = WebpBitstreamFormat.Lossless;
                    imageBytes = WebpChunkReader.ReadPayload(stream, header.Size);
                    break;

                case "ALPH":
                    alphaBytes = WebpChunkReader.ReadPayload(stream, header.Size);
                    break;

                case "ICCP":
                    WebpMetadataReader.AddIcc(metadata, WebpChunkReader.ReadPayload(stream, header.Size));
                    break;

                case "EXIF":
                    WebpMetadataReader.AddExif(metadata, WebpChunkReader.ReadPayload(stream, header.Size));
                    break;

                case "XMP ":
                    WebpMetadataReader.AddXmp(metadata, WebpChunkReader.ReadPayload(stream, header.Size));
                    break;

                case "ANIM":
                case "ANMF":
                    sawAnimationChunk = true;
                    WebpChunkReader.SkipPayload(stream, header.Size);
                    break;

                default:
                    // Unrecognized chunk (e.g. a future extension) — skip, matching PNG's ancillary-chunk leniency.
                    WebpChunkReader.SkipPayload(stream, header.Size);
                    break;
            }
        }

        if (hasAnimationFlag || sawAnimationChunk)
        {
            throw new WebpDecodingException("Animated WebP is not supported yet; animation support is planned but not yet implemented.");
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
