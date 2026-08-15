namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Inverts VP8L's color-indexing (palette) transform: the decoded pixel stream's green channel holds palette
/// indices instead of real colors, and — when the palette has 16 or fewer entries — those indices are packed
/// several per byte to shrink the entropy-coded stream, at the cost of also shrinking the stream's width
/// (<c>bitsPerPixel = 8 &gt;&gt; transform.Bits</c>, so <c>transform.Bits</c> 3/2/1/0 pack 8/4/2/1 indices per
/// byte). <see cref="ApplyInverse"/> must both unpack any sub-byte indices <em>and</em> expand the stream
/// back out to the image's full width in a single pass.
/// </summary>
internal static class Vp8LColorIndexingTransform
{
    /// <summary>
    /// Expands the palette sub-image's per-channel deltas into absolute colors via a running cumulative sum
    /// (mod 256, no cross-channel carry — <see cref="Vp8LPixelMath.AddWrapping"/>), then pads the result up
    /// to <c>1 &lt;&lt; (8 &gt;&gt; bits)</c> entries with implicit opaque-black zero entries — matching
    /// libwebp's <c>ExpandColorMap</c>, so a packed index built from stray high bits in a byte (always
    /// possible when fewer than 8 bits are actually used per pixel) still lands on a valid, defined entry.
    /// </summary>
    public static uint[] ExpandPalette(uint[] rawDeltas, int numColors, int bits)
    {
        int finalCount = 1 << (8 >> bits);
        var palette = new uint[finalCount];

        if (numColors > 0)
        {
            palette[0] = rawDeltas[0];
            for (int i = 1; i < numColors; i++)
            {
                palette[i] = Vp8LPixelMath.AddWrapping(palette[i - 1], rawDeltas[i]);
            }
        }

        return palette;
    }

    /// <summary>Expands <paramref name="src"/> (the decoded, possibly index-packed pixel stream, <c>transform</c>'s reduced width) into a fresh full-<c>transform.Xsize</c>-wide ARGB buffer.</summary>
    public static uint[] ApplyInverse(ReadOnlySpan<uint> src, Vp8LTransform transform)
    {
        int fullWidth = transform.Xsize;
        int height = transform.Ysize;
        int bits = transform.Bits;
        var palette = transform.Data!;
        int bitsPerPixel = 8 >> bits;

        var dst = new uint[(long)fullWidth * height];

        if (bitsPerPixel == 8)
        {
            // No packing: exactly one index per pixel, same width as the destination.
            for (int i = 0; i < dst.Length; i++)
            {
                int index = (int)((src[i] >> 8) & 0xFF);
                dst[i] = palette[index];
            }

            return dst;
        }

        int pixelsPerByte = 1 << bits;
        uint bitMask = (uint)((1 << bitsPerPixel) - 1);
        int srcWidth = Vp8LMetaHuffmanImage.SubSampleSize(fullWidth, bits);

        for (int y = 0; y < height; y++)
        {
            int srcRowBase = y * srcWidth;
            int dstRowBase = y * fullWidth;
            uint packed = 0;
            int srcX = 0;

            for (int x = 0; x < fullWidth; x++)
            {
                if (x % pixelsPerByte == 0)
                {
                    packed = (src[srcRowBase + srcX] >> 8) & 0xFF;
                    srcX++;
                }

                int index = (int)(packed & bitMask);
                dst[dstRowBase + x] = palette[index];
                packed >>= bitsPerPixel;
            }
        }

        return dst;
    }
}
