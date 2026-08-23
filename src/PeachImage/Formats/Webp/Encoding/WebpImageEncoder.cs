namespace PeachImage.Formats.Webp.Encoding;

/// <summary>
/// Orchestrates a full still-image WebP encode: hands the pixels to <see cref="WebpFrameEncoder"/> for the
/// <c>VP8L</c>/<c>VP8 </c> payload chunk, then assembles the RIFF container -- simple format (a bare
/// <c>VP8L</c>/<c>VP8 </c> chunk) when there's no metadata to carry, extended format (<c>VP8X</c> + optional
/// <c>ICCP</c>/<c>EXIF</c>/<c>XMP </c>) otherwise. No <c>ALPH</c> chunk is ever written: VP8L always carries
/// alpha internally via its own <c>alpha_is_used</c> header bit, and VP8 lossy encode is never selected for an
/// alpha-bearing image (see <see cref="WebpEncoderOptions.Lossless"/>'s remarks) until a follow-on adds it.
/// See <see cref="WebpAnimationEncoder"/> for the multi-frame counterpart.
/// </summary>
internal static class WebpImageEncoder
{
    public static void Encode(Image image, Stream stream, WebpEncoderOptions options)
    {
        var frame = WebpFrameEncoder.Encode(image, options);

        byte[]? icc = null;
        byte[]? exif = null;
        byte[]? xmp = null;
        if (options.IncludeMetadata)
        {
            (icc, exif, xmp) = WebpMetadataWriter.CollectProfiles(image.Metadata);
        }

        WriteContainer(stream, image.Width, image.Height, frame.HasAlpha, frame.FourCc, frame.Payload, icc, exif, xmp);
    }

    /// <summary>
    /// Writes the RIFF container around whichever payload chunk was produced -- <c>VP8L</c> or <c>VP8 </c>
    /// (<paramref name="fourCc"/>/<paramref name="payload"/>). The container format itself doesn't distinguish
    /// lossy from lossless (there is no "is this lossless" bit in the <c>VP8X</c> flags byte -- a decoder tells
    /// the two apart purely by which of the two chunk FourCCs is present), so this needed no changes beyond
    /// parameterizing which chunk gets written.
    /// </summary>
    private static void WriteContainer(Stream stream, int width, int height, bool hasAlpha, string fourCc, byte[] payload, byte[]? icc, byte[]? exif, byte[]? xmp)
    {
        bool extended = icc is not null || exif is not null || xmp is not null;

        byte[]? vp8X = extended
            ? WebpContainerWriter.BuildVp8XPayload(width, height, hasAlpha, hasAnimation: false, icc is not null, exif is not null, xmp is not null)
            : null;

        long riffSize = 4; // "WEBP" form type.
        riffSize += SizeIfPresent(vp8X);
        riffSize += SizeIfPresent(icc);
        riffSize += WebpContainerWriter.GetFramedChunkSize(payload.Length);
        riffSize += SizeIfPresent(exif);
        riffSize += SizeIfPresent(xmp);

        WebpContainerWriter.WriteRiffHeader(stream, (uint)riffSize);

        if (vp8X is not null)
        {
            WebpContainerWriter.WriteChunk(stream, "VP8X", vp8X);
        }

        if (icc is not null)
        {
            WebpContainerWriter.WriteChunk(stream, "ICCP", icc);
        }

        WebpContainerWriter.WriteChunk(stream, fourCc, payload);

        if (exif is not null)
        {
            WebpContainerWriter.WriteChunk(stream, "EXIF", exif);
        }

        if (xmp is not null)
        {
            WebpContainerWriter.WriteChunk(stream, "XMP ", xmp);
        }
    }

    private static long SizeIfPresent(byte[]? payload) => payload is null ? 0 : WebpContainerWriter.GetFramedChunkSize(payload.Length);
}
