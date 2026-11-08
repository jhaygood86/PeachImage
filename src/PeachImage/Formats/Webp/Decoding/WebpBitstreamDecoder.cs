using PeachImage.Formats.Webp.Decoding.Alpha;
using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Decoding.Vp8L;
using PeachImage.Internal;

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
    private static unsafe Image DecodeLossy(byte[] bitstreamData, byte[]? alphaData)
    {
        if (!WebpBitstreamHeaderPeek.TryPeekVp8(bitstreamData, out int width, out int height))
        {
            throw new WebpDecodingException("Malformed VP8 chunk: missing or invalid keyframe start code.");
        }

        var format = alphaData is null ? PixelFormat.Rgb24 : PixelFormat.Rgba32;
        int rowBytes = checked(width * format.GetBytesPerPixel());

        // The frame is converted straight into the pooled buffer that becomes the image: no exact-size intermediate array, no copy.
        byte[]? pooledPixels = ImageBufferPool.Shared.Rent(checked(rowBytes * height));
        try
        {
            fixed (byte* pixels = pooledPixels)
            {
                DecodeLossyInto(bitstreamData, alphaData, new WebpOutput((nint)pixels, rowBytes, width, height, format));
            }

            var image = Image.FromBuffer(width, height, format, pooledPixels, owned: true);
            pooledPixels = null;
            return image;
        }
        finally
        {
            if (pooledPixels is not null)
            {
                ImageBufferPool.Shared.Return(pooledPixels);
            }
        }
    }

    /// <summary>Decodes a single bitstream straight into <paramref name="output"/>'s memory.</summary>
    public static void DecodeInto(WebpBitstreamFormat format, byte[] bitstreamData, byte[]? alphaData, WebpOutput output)
    {
        if (format == WebpBitstreamFormat.Lossless)
        {
            Vp8LDecoder.DecodeInto(bitstreamData, output);
        }
        else
        {
            DecodeLossyInto(bitstreamData, alphaData, output);
        }
    }

    private static void DecodeLossyInto(byte[] bitstreamData, byte[]? alphaData, WebpOutput output)
    {
        if (!WebpBitstreamHeaderPeek.TryPeekVp8(bitstreamData, out int width, out int height))
        {
            throw new WebpDecodingException("Malformed VP8 chunk: missing or invalid keyframe start code.");
        }

        output.Validate(width, height, alphaData is null ? PixelFormat.Rgb24 : PixelFormat.Rgba32);

        byte[]? alphaPlane = alphaData is { } data ? WebpAlphaDecoder.Decode(data, width, height) : null;
        Vp8FrameDecoder.Instance.DecodeInto(bitstreamData, alphaPlane, output);
    }
}
