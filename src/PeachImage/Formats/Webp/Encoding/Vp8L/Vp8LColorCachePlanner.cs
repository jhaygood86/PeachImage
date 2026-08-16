using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>
/// Chooses whether a VP8L color cache is worth using for a given pixel buffer, and if so, how large. A
/// cheap heuristic rather than an exhaustive search: simulates hit rates for a fixed candidate set of cache
/// sizes (skipping the smallest sizes, which thrash and rarely win) and picks whichever scores best by a
/// flat nominal bit-cost estimate.
/// </summary>
internal static class Vp8LColorCachePlanner
{
    private static readonly int[] CandidateBits = [8, 10, 11];

    private const double CostLiteralBits = 32;
    private const double CostCacheHitBits = 10;

    /// <summary>
    /// Returns the cache-bits value to declare, or 0 to disable the color cache entirely. Callers pass
    /// <paramref name="useColorCacheOption"/><c>: false</c> both for <see cref="WebpEncoderOptions.UseColorCache"/>
    /// being off and for streams where a cache is structurally pointless (e.g. a color-indexing-narrowed
    /// main image, or a transform parameter sub-image) — once pixels are palette indices, the alphabet is
    /// already tiny and highly compressible on its own, and a color cache adds pure declaration overhead
    /// for essentially no benefit.
    /// </summary>
    public static int ChooseCacheBits(ReadOnlySpan<uint> pixels, bool useColorCacheOption)
    {
        if (!useColorCacheOption || pixels.IsEmpty)
        {
            return 0;
        }

        int bestBits = 0;
        double bestScore = 0;

        foreach (int bits in CandidateBits)
        {
            double score = SimulateScore(pixels, bits);
            if (score > bestScore)
            {
                bestScore = score;
                bestBits = bits;
            }
        }

        return bestBits;
    }

    private static double SimulateScore(ReadOnlySpan<uint> pixels, int cacheBits)
    {
        var cache = new Vp8LColorCache(cacheBits);
        long hits = 0;

        foreach (uint pixel in pixels)
        {
            if (cache.TryGetHitIndex(pixel, out _))
            {
                hits++;
            }

            cache.Insert(pixel);
        }

        // Fixed overhead: the cache-bits declaration itself, plus a rough per-entry cost for transmitting
        // the extra green-alphabet code lengths a cache adds.
        double overhead = 5 + ((1 << cacheBits) * 0.5);
        return (hits * (CostLiteralBits - CostCacheHitBits)) - overhead;
    }
}
