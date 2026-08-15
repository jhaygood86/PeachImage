using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// VP8L's core per-pixel decode loop: reads the (optional) color cache declaration and Huffman code
/// definitions, then walks the pixel stream. Recursively reusable for the meta-Huffman entropy sub-image and
/// every transform's parameter sub-image (<c>allowRecursion: false</c> for those — a color cache and a
/// further nested meta-Huffman image are only meaningful/permitted at the top level).
/// </summary>
internal static class Vp8LPixelDecoder
{
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int LengthCodeLimit = NumLiteralCodes + NumLengthCodes;

    /// <summary>
    /// Decodes one VP8L pixel stream. <paramref name="rentFromPool"/> should only ever be set by the single
    /// top-level (<paramref name="allowRecursion"/><c>: true</c>) caller for the image's real, potentially
    /// large pixel buffer — <see cref="WebpBufferPool.SharedUInt32"/> exists precisely because that one buffer
    /// is the dominant source of large-object-heap churn in VP8L decode; every recursive sub-image call here
    /// (meta-Huffman entropy image, predictor/color-transform tile parameters, the color-indexing palette) is
    /// always small, so pooling them would add rent/return bookkeeping for no measurable benefit. When
    /// <paramref name="rentFromPool"/> is <see langword="true"/>, the returned array may be larger than
    /// <c>width*height</c> (an <see cref="System.Buffers.ArrayPool{T}"/> contract) — callers MUST bound every
    /// use of it to exactly <c>width*height</c> elements (e.g. via <c>.AsSpan(0, width*height)</c>), never rely
    /// on the array's own <c>.Length</c>, and are responsible for returning it to the pool once done.
    /// </summary>
    public static uint[] DecodeImageStream(Vp8LBitReader reader, int width, int height, bool allowRecursion, bool rentFromPool = false)
    {
        if (width <= 0 || height <= 0)
        {
            throw new WebpDecodingException($"Invalid VP8L image stream dimensions {width}x{height}.");
        }

        long pixelCount = (long)width * height;
        if (pixelCount > WebpDecodingLimits.MaxPixelCount)
        {
            throw new WebpDecodingException($"VP8L image stream {width}x{height} exceeds the maximum supported pixel count.");
        }

        int colorCacheBits = 0;
        if (reader.ReadBits(1) != 0)
        {
            colorCacheBits = (int)reader.ReadBits(4);
            if (colorCacheBits is < 1 or > WebpDecodingLimits.MaxColorCacheBits)
            {
                throw new WebpDecodingException($"Invalid VP8L color cache bits {colorCacheBits}; must be in [1, {WebpDecodingLimits.MaxColorCacheBits}].");
            }
        }

        var groups = Vp8LMetaHuffmanImage.ReadGroups(reader, width, height, colorCacheBits, allowRecursion, out var metaImage);
        var colorCache = colorCacheBits > 0 ? new Vp8LColorCache(colorCacheBits) : null;
        int colorCacheLimit = LengthCodeLimit + (colorCacheBits > 0 ? 1 << colorCacheBits : 0);

        var pixels = rentFromPool ? WebpBufferPool.SharedUInt32.Rent((int)pixelCount) : new uint[pixelCount];
        var activeGroup = groups[0];

        int col = 0;
        int row = 0;
        long pos = 0;

        while (pos < pixelCount)
        {
            if (reader.IsOverBudget)
            {
                // Truncated stream: stop decoding, mirroring GifLzwDecoder's "stop early on truncation rather
                // than throw" convention. The undecoded remainder is zeroed after the loop.
                break;
            }

            if (metaImage != null)
            {
                activeGroup = groups[metaImage.GetGroupIndex(col, row)];
            }

            int code = activeGroup.Green.DecodeMain(reader);

            if (code < NumLiteralCodes)
            {
                int red = activeGroup.Red.DecodeMain(reader);
                int blue = activeGroup.Blue.DecodeMain(reader);
                int alpha = activeGroup.Alpha.DecodeMain(reader);
                uint argb = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)code << 8) | (uint)blue;

                pixels[pos] = argb;
                colorCache?.Insert(argb);

                pos++;
                col++;
                if (col >= width)
                {
                    col = 0;
                    row++;
                }
            }
            else if (code < LengthCodeLimit)
            {
                int lengthSymbol = code - NumLiteralCodes;
                int length = Vp8LBackwardReferenceTables.DecodePrefixCodeValue(lengthSymbol, reader);
                int distanceSymbol = activeGroup.Distance.DecodeMain(reader);
                int distanceCode = Vp8LBackwardReferenceTables.DecodePrefixCodeValue(distanceSymbol, reader);
                int distance = Vp8LBackwardReferenceTables.PlaneCodeToDistance(width, distanceCode);

                if (distance > pos || length > pixelCount - pos)
                {
                    // A backward reference reaching before the start of the buffer, or past its end, can
                    // only come from a corrupt/truncated stream -- stop early rather than throw, same
                    // leniency as the truncation case above.
                    break;
                }

                Vp8LBackwardCopier.CopyPixels(pixels, (int)pos, distance, length);

                if (colorCache != null)
                {
                    for (int i = 0; i < length; i++)
                    {
                        colorCache.Insert(pixels[pos + i]);
                    }
                }

                pos += length;
                col += length;
                while (col >= width)
                {
                    col -= width;
                    row++;
                }
            }
            else if (code < colorCacheLimit)
            {
                int key = code - LengthCodeLimit;
                pixels[pos] = colorCache!.Lookup(key);

                pos++;
                col++;
                if (col >= width)
                {
                    col = 0;
                    row++;
                }
            }
            else
            {
                // Code outside every valid range for this stream's configuration -- corrupt data.
                break;
            }
        }

        // Every `break` above leaves pixels [pos, pixelCount) never written. That region has to be zeroed
        // explicitly, because `pixels` may be a pool-rented array still holding the *previous* decode's image:
        // without this, a truncated or corrupt file would surface another image's pixels as its own. Only
        // reached on malformed input -- a stream that decodes fully leaves nothing to clear.
        if (pos < pixelCount)
        {
            pixels.AsSpan((int)pos, (int)(pixelCount - pos)).Clear();
        }

        return pixels;
    }
}
