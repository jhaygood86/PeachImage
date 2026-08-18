using PeachImage.Formats.Webp.Decoding.Alpha;
using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Formats.Webp.Decoding;

/// <summary>
/// Decodes a single VP8/VP8L bitstream (+ optional <c>ALPH</c> plane for lossy) into an <see cref="Image"/>.
/// Shared by <see cref="WebpDecoder"/>'s single-image path and <see cref="WebpAnimationReader"/>'s
/// per-<c>ANMF</c>-frame decode, so both invoke the identical decode sequence.
/// </summary>
internal static class WebpBitstreamDecoder
{
    public static Image Decode(WebpBitstreamFormat format, byte[] bitstreamData, byte[]? alphaData) =>
        format == WebpBitstreamFormat.Lossless
            ? Vp8LDecoder.Decode(bitstreamData)
            : DecodeLossy(bitstreamData, alphaData);

    /// <summary>
    /// Decodes the alpha plane (if any) at the bitstream's own dimensions first, then hands it into the VP8
    /// frame decoder so pixel conversion can emit RGBA32 directly instead of building an RGB24 image and
    /// widening it afterward -- see <see cref="Vp8FrameDecoder.ProduceRgbFrame"/>'s remarks for why that
    /// matters. Dimensions are read via a cheap peek rather than waiting on the frame decoder's own parse,
    /// since alpha decode needs them first; both read the identical bytes, so they can't disagree.
    /// </summary>
    private static Image DecodeLossy(byte[] bitstreamData, byte[]? alphaData)
    {
        byte[]? alphaPlane = null;

        if (alphaData is { } data)
        {
            if (!WebpBitstreamHeaderPeek.TryPeekVp8(bitstreamData, out int width, out int height))
            {
                throw new WebpDecodingException("Malformed VP8 chunk: missing or invalid keyframe start code.");
            }

            alphaPlane = WebpAlphaDecoder.Decode(data, width, height);
        }

        var frame = Vp8FrameDecoder.Instance.Decode(bitstreamData, alphaPlane);
        return Image.FromBuffer(frame.Width, frame.Height, frame.PixelFormat, frame.Pixels);
    }
}
