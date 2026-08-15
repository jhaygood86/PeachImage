using PeachImage.Formats.Webp.Decoding.Vp8L;
using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding.Alpha;

/// <summary>
/// Decodes an <c>ALPH</c> chunk's payload into a flat, tightly-packed <c>width*height</c> alpha plane:
/// either raw uncompressed samples, or a cut-down VP8L bitstream (reusing <see cref="Vp8LDecoder.DecodeAlphaSubstream"/>'s
/// full pixel-decode machinery, reading only its green channel) — followed by filter-method reversal
/// (None/Horizontal/Vertical/Gradient) via <see cref="WebpAlphaFilter"/>.
/// </summary>
internal static class WebpAlphaDecoder
{
    public static byte[] Decode(ReadOnlyMemory<byte> alphBytes, int width, int height)
    {
        if (alphBytes.Length < 1)
        {
            throw new WebpDecodingException("ALPH chunk is empty.");
        }

        var header = WebpAlphaHeader.Parse(alphBytes.Span[0]);
        var payload = alphBytes[1..];

        byte[] plane = header.CompressionMethod switch
        {
            WebpAlphaCompressionMethod.Uncompressed => DecodeUncompressed(payload.Span, width, height),
            WebpAlphaCompressionMethod.Lossless => DecodeLossless(payload, width, height),
            _ => throw new WebpDecodingException($"Unsupported WebP alpha compression method {header.CompressionMethod}."),
        };

        WebpAlphaFilter.Reverse(header.FilterMethod, plane, width, height);
        return plane;
    }

    private static byte[] DecodeUncompressed(ReadOnlySpan<byte> payload, int width, int height)
    {
        long expected = (long)width * height;
        if (payload.Length < expected)
        {
            throw new WebpDecodingException($"WebP ALPH chunk (uncompressed) is too short: expected {expected} bytes, got {payload.Length}.");
        }

        return payload[..(int)expected].ToArray();
    }

    private static byte[] DecodeLossless(ReadOnlyMemory<byte> payload, int width, int height)
    {
        uint[] argb = Vp8LDecoder.DecodeAlphaSubstream(payload, width, height, out bool isPooled);
        try
        {
            byte[] plane = new byte[(long)width * height <= int.MaxValue ? width * height : throw new WebpDecodingException("WebP alpha plane too large.")];

            // The alpha lossless substream reuses VP8L's full general pixel-decode pipeline; only the green
            // channel byte of each ARGB pixel is meaningful here (alpha/red/blue of the inner decode are
            // unused noise the encoder emits to satisfy the general-purpose ARGB pixel format). ARGB is
            // packed A<<24 | R<<16 | G<<8 | B, matching libwebp's own internal pixel representation. `argb`
            // may be larger than `plane` (a pooled, possibly-oversized array) -- bounding the loop by
            // `plane.Length`, not `argb.Length`, is what makes that safe.
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] = (byte)(argb[i] >> 8);
            }

            return plane;
        }
        finally
        {
            if (isPooled)
            {
                WebpBufferPool.SharedUInt32.Return(argb);
            }
        }
    }
}
