using PeachImage.Formats.Webp.Decoding.Vp8L;
using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>
/// Walks a flat ARGB pixel array and emits the VP8L pixel-stream token sequence (literals, backward
/// references, color-cache-index hits) that <see cref="Decoding.Vp8L.Vp8LPixelDecoder"/>'s decode loop would
/// need to reproduce it exactly — the encode-side mirror of that loop.
/// </summary>
internal static class Vp8LTokenizer
{
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int LengthCodeLimit = NumLiteralCodes + NumLengthCodes;

    // Flat, nominal per-symbol bit-cost estimates used only to compare a candidate match's cost against the
    // literal/cache-hit pixels it would replace -- not real Huffman code lengths, which aren't known until
    // after a full tokenization pass (a chicken-and-egg problem this static heuristic deliberately avoids
    // resolving via iteration, in exchange for a single tokenize pass).
    private const double CostLiteralBits = 32;
    private const double CostCacheHitBits = 10;
    private const double CostMatchBaseBits = 16;

    /// <summary>
    /// Tokenizes <paramref name="pixels"/>' first <paramref name="pixelCount"/> elements. The returned array
    /// is rented from <see cref="Vp8LTokenPool.Shared"/> and may be longer than <paramref name="tokenCount"/>
    /// (an <see cref="System.Buffers.ArrayPool{T}"/> contract) -- callers must bound every use to exactly
    /// <paramref name="tokenCount"/> entries and are responsible for returning it once done.
    /// </summary>
    public static Vp8LToken[] Tokenize(uint[] pixels, int pixelCount, int width, int cacheBits, int hashBits, int maxChainLength, int giveUpAfterFruitlessProbes, out int tokenCount, out Vp8LTokenHistograms histograms)
    {
        int greenAlphabetSize = LengthCodeLimit + (cacheBits > 0 ? 1 << cacheBits : 0);
        var green = new int[greenAlphabetSize];
        var red = new int[NumLiteralCodes];
        var blue = new int[NumLiteralCodes];
        var alpha = new int[NumLiteralCodes];
        var distance = new int[WebpDecodingLimits.DistanceAlphabetSize];

        // Token count is always <= pixelCount (every token consumes at least one pixel), so this is a safe,
        // tight upper bound -- rented rather than a plain array so a long-running process encoding many
        // images doesn't pay a fresh large-object-heap allocation (~40 MiB for a 1080p image) every call.
        var tokens = Vp8LTokenPool.Shared.Rent(Math.Max(pixelCount, 1));
        int count = 0;
        using var matchFinder = new Vp8LMatchFinder(pixels, pixelCount, hashBits, maxChainLength, giveUpAfterFruitlessProbes);
        var distanceMapper = new Vp8LDistanceMapper(width);
        var cache = cacheBits > 0 ? new Vp8LColorCache(cacheBits) : null;

        int pos = 0;
        while (pos < pixelCount)
        {
            int hitIndex = 0;
            bool isCacheHit = cache is not null && cache.TryGetHitIndex(pixels[pos], out hitIndex);
            bool hasMatch = matchFinder.TryFindMatch(pos, out int matchDistance, out int matchLength);

            int planeCode = 0;
            bool useMatch = false;
            if (hasMatch)
            {
                planeCode = distanceMapper.DistanceToPlaneCode(matchDistance);
                double matchCost = EstimateMatchCost(matchLength, planeCode);
                double alternativeCostPerPixel = isCacheHit ? CostCacheHitBits : CostLiteralBits;
                useMatch = matchCost < matchLength * alternativeCostPerPixel;
            }

            if (useMatch)
            {
                tokens[count++] = new Vp8LToken { Kind = Vp8LTokenKind.BackwardReference, Length = matchLength, PlaneCode = planeCode };
                AccumulateMatchHistogram(green, distance, matchLength, planeCode);

                for (int i = 0; i < matchLength; i++)
                {
                    matchFinder.Insert(pos + i);
                    cache?.Insert(pixels[pos + i]);
                }

                pos += matchLength;
            }
            else if (isCacheHit)
            {
                tokens[count++] = new Vp8LToken { Kind = Vp8LTokenKind.CacheIndex, CacheIndex = hitIndex };
                green[LengthCodeLimit + hitIndex]++;

                // Decode never re-inserts a cache-index-decoded pixel into the cache (Vp8LPixelDecoder's
                // matching branch has no colorCache.Insert call), so the encoder's simulated cache state
                // must skip it too to stay bit-for-bit in sync with what the decoder's cache will hold.
                matchFinder.Insert(pos);
                pos += 1;
            }
            else
            {
                uint pixel = pixels[pos];
                tokens[count++] = new Vp8LToken { Kind = Vp8LTokenKind.Literal, Argb = pixel };

                byte a = (byte)(pixel >> 24);
                byte r = (byte)(pixel >> 16);
                byte g = (byte)(pixel >> 8);
                byte b = (byte)pixel;

                green[g]++;
                red[r]++;
                blue[b]++;
                alpha[a]++;

                matchFinder.Insert(pos);
                cache?.Insert(pixel);
                pos += 1;
            }
        }

        tokenCount = count;
        histograms = new Vp8LTokenHistograms { Green = green, Red = red, Blue = blue, Alpha = alpha, Distance = distance };
        return tokens;
    }

    private static double EstimateMatchCost(int length, int planeCode)
    {
        var (_, _, lengthExtraBits) = Vp8LPrefixCodeEncoder.EncodePrefixCodeValue(length);
        var (_, _, distanceExtraBits) = Vp8LPrefixCodeEncoder.EncodePrefixCodeValue(planeCode);
        return CostMatchBaseBits + lengthExtraBits + distanceExtraBits;
    }

    private static void AccumulateMatchHistogram(int[] green, int[] distanceHistogram, int length, int planeCode)
    {
        var (lengthSymbol, _, _) = Vp8LPrefixCodeEncoder.EncodePrefixCodeValue(length);
        var (distanceSymbol, _, _) = Vp8LPrefixCodeEncoder.EncodePrefixCodeValue(planeCode);
        green[NumLiteralCodes + lengthSymbol]++;
        distanceHistogram[distanceSymbol]++;
    }
}
