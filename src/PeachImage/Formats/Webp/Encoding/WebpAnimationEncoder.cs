using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Encoding;

/// <summary>
/// Orchestrates a full animated WebP encode: <c>VP8X</c> (animation bit set) → <c>ANIM</c> → one <c>ANMF</c>
/// per frame, each wrapping a <c>VP8L</c>/<c>VP8 </c> payload produced by <see cref="WebpFrameEncoder"/> --
/// the same per-frame encoder <see cref="WebpImageEncoder"/> uses for still images. No <c>ICCP</c>/<c>EXIF</c>/
/// <c>XMP </c> chunks are written: <see cref="AnimatedImage"/> carries no metadata to source them from.
/// </summary>
internal static class WebpAnimationEncoder
{
    public static void Encode(AnimatedImage image, Stream stream, WebpEncoderOptions options)
    {
        if (image.Width > WebpDecodingLimits.MaxDimension || image.Height > WebpDecodingLimits.MaxDimension
            || (long)image.Width * image.Height > WebpDecodingLimits.MaxPixelCount)
        {
            throw new WebpEncodingException($"WebP image {image.Width}x{image.Height} exceeds the maximum supported dimensions.");
        }

        // A single forward pass over image.Frames, unlike GifImageEncoder.EncodeAnimation: WebP has no
        // cross-frame shared state (no global palette to build from full-corpus statistics), so each frame's
        // pixels can be fully consumed into an owned ANMF chunk before the next frame is pulled -- safe even
        // though a decode-sourced frame.Image aliases mutable compositor state, since nothing outlives this
        // loop iteration.
        var anmfChunks = new List<byte[]>();
        bool anyAlpha = false;

        foreach (var frame in image.Frames)
        {
            if (frame.Image.Width != image.Width || frame.Image.Height != image.Height)
            {
                throw new WebpEncodingException("All frames of an animated WebP must share the same dimensions as the animation canvas.");
            }

            var payload = WebpFrameEncoder.Encode(frame.Image, options);
            anyAlpha |= payload.HasAlpha;

            anmfChunks.Add(BuildAnmfChunk(frame, payload));
        }

        if (anmfChunks.Count == 0)
        {
            throw new WebpEncodingException("Cannot encode an animated WebP with no frames.");
        }

        byte[] vp8X = WebpContainerWriter.BuildVp8XPayload(image.Width, image.Height, anyAlpha, hasAnimation: true, hasIcc: false, hasExif: false, hasXmp: false);
        byte[] anim = WebpContainerWriter.BuildAnimPayload(image.LoopCount);

        long riffSize = 4; // "WEBP" form type.
        riffSize += WebpContainerWriter.GetFramedChunkSize(vp8X.Length);
        riffSize += WebpContainerWriter.GetFramedChunkSize(anim.Length);
        foreach (byte[] anmf in anmfChunks)
        {
            riffSize += WebpContainerWriter.GetFramedChunkSize(anmf.Length);
        }

        WebpContainerWriter.WriteRiffHeader(stream, (uint)riffSize);
        WebpContainerWriter.WriteChunk(stream, "VP8X", vp8X);
        WebpContainerWriter.WriteChunk(stream, "ANIM", anim);
        foreach (byte[] anmf in anmfChunks)
        {
            WebpContainerWriter.WriteChunk(stream, "ANMF", anmf);
        }
    }

    /// <summary>Builds one <c>ANMF</c> chunk's payload: the 16-byte frame header followed by the frame's nested <c>VP8 </c>/<c>VP8L</c> chunk.</summary>
    private static byte[] BuildAnmfChunk(AnimatedImageFrame frame, WebpFramePayload payload)
    {
        int durationMs = (int)Math.Clamp(frame.Duration.TotalMilliseconds, 0, 0xFF_FFFF);
        bool disposeToBackground = frame.Disposal == FrameDisposalMethod.RestoreToBackground;
        byte[] header = WebpContainerWriter.BuildAnmfFrameHeader(frame.Image.Width, frame.Image.Height, durationMs, disposeToBackground);

        using var buffer = new MemoryStream();
        buffer.Write(header);
        WebpContainerWriter.WriteChunk(buffer, payload.FourCc, payload.Payload);
        return buffer.ToArray();
    }
}
