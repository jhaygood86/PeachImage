using System.Collections.Concurrent;

namespace PeachImage.Formats.Png.Internal;

/// <summary>
/// Builds the lookup table mapping a raw, unscaled PNG sample (grayscale/truecolor color-type
/// channels only — never palette indices, never alpha) to its final output channel value: for bit
/// depths below 8, this both scales to the 0-255 display range (depth 1: {0,255}; depth 2:
/// {0,85,170,255}; depth 4: 17-multiples) and, optionally, applies gamma correction; for depths 8/16
/// it is scale-neutral (identity when no gamma is requested) and applies gamma correction only.
/// tRNS-key comparison must always happen against the raw sample, before this table is consulted
/// (spec §11.3.2.1 defines the key in the same unscaled range as the sample).
/// </summary>
internal static class PngSampleLut
{
    /// <summary>
    /// The one bit depth whose table is large enough to be worth keeping: at depth 16 it is
    /// <c>ushort[65536]</c> — 128 KB, over the 85 KB large-object threshold — so every decode of a
    /// 16-bit PNG put a fresh one straight onto the Large Object Heap, where it can only be reclaimed
    /// by a gen2 collection. Sampled allocation over a 26-document corpus measured 731 MB in this
    /// method alone, a quarter of all large-object traffic.
    /// </summary>
    /// <remarks>
    /// Depths 1/2/4/8 are deliberately not cached: their tables are at most 256 entries (512 bytes),
    /// live and die in gen0, and are cheaper to rebuild than to look up.
    /// </remarks>
    private const int CachedBitDepth = 16;

    /// <summary>
    /// How many distinct gamma exponents <see cref="CorrectedTables"/> will hold, at 128 KB each.
    /// </summary>
    /// <remarks>
    /// Bounded because <c>gAMA</c> comes from the file, so an unusual or hostile corpus could
    /// otherwise grow the cache without limit. Past the cap a miss simply builds and returns without
    /// adding, costing exactly what the uncached code costs. The cap holds strictly: the count is
    /// tested and the entry added under <see cref="CorrectedTablesLock"/>, so concurrent misses
    /// cannot each pass a stale count check and push the dictionary past it.
    /// </remarks>
    internal const int MaxEntries = 8;

    /// <summary>
    /// The depth-16 table for "no gamma correction", held separately and never evicted.
    /// </summary>
    /// <remarks>
    /// This is the overwhelmingly common case — <see cref="PngDecoderOptions.ScreenGamma"/> is unset
    /// by default, and without it no embedded <c>gAMA</c>/<c>sRGB</c> value changes a single sample —
    /// and its key is not attacker-controlled, so it does not belong in the bounded dictionary. Keying
    /// the cache on the raw <c>(fileGamma, screenGamma)</c> pair instead spent one slot per distinct
    /// embedded gamma on byte-identical identity tables, which is how a corpus of ordinary
    /// gamma-bearing files (this repo's own pngsuite has six) could fill the cap without a single
    /// entry that saves any work.
    /// </remarks>
    private static readonly Lazy<ushort[]> IdentityTable =
        new(() => BuildUncached(CachedBitDepth, null), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Depth-16 tables that actually apply a correction, keyed by the exponent that produced them.
    /// </summary>
    /// <remarks>
    /// The exponent, not the <c>(fileGamma, screenGamma)</c> pair it came from, is what determines the
    /// table's contents, so every pair with the same ratio shares one entry.
    /// </remarks>
    private static readonly ConcurrentDictionary<double, ushort[]> CorrectedTables = new();

    private static readonly object CorrectedTablesLock = new();

    public static ushort[] Build(int bitDepth, double? fileGamma, double? screenGamma)
    {
        var gammaExponent = EffectiveGammaExponent(fileGamma, screenGamma);

        if (bitDepth != CachedBitDepth)
        {
            return BuildUncached(bitDepth, gammaExponent);
        }

        if (gammaExponent is not { } exponent)
        {
            return IdentityTable.Value;
        }

        if (CorrectedTables.TryGetValue(exponent, out var cached))
        {
            return cached;
        }

        var built = BuildUncached(CachedBitDepth, exponent);

        // Taken only on a miss, and only for depth 16, so it is off the decode hot path. Holding it
        // across the count check is what makes the cap an actual bound rather than a hint.
        lock (CorrectedTablesLock)
        {
            if (CorrectedTables.Count < MaxEntries)
            {
                CorrectedTables.TryAdd(exponent, built);
            }
        }

        return built;
    }

    /// <summary>
    /// The exponent the table is built with, or <see langword="null"/> when the inputs leave every
    /// sample unchanged: a file gamma with no screen gamma to correct it against corrects nothing.
    /// </summary>
    private static double? EffectiveGammaExponent(double? fileGamma, double? screenGamma) =>
        fileGamma is { } fg && screenGamma is > 0 ? fg / screenGamma : null;

    private static ushort[] BuildUncached(int bitDepth, double? gammaExponent)
    {
        int inputMax = (1 << bitDepth) - 1;
        int outputMax = bitDepth <= 8 ? 255 : 65535;
        var lut = new ushort[inputMax + 1];

        for (int i = 0; i <= inputMax; i++)
        {
            double normalized = (double)i / inputMax;
            double corrected = gammaExponent is { } exponent ? Math.Pow(normalized, exponent) : normalized;
            lut[i] = (ushort)Math.Clamp(Math.Round(corrected * outputMax, MidpointRounding.AwayFromZero), 0, outputMax);
        }

        return lut;
    }

    /// <summary>
    /// Test hook: how many gamma-corrected tables are cached, so a test can assert the cap holds.
    /// </summary>
    internal static int CachedCorrectedTableCount => CorrectedTables.Count;

    /// <summary>
    /// Test hook: empties the bounded cache, so a test starts from a known state instead of
    /// inheriting whatever the rest of the suite left behind.
    /// </summary>
    /// <remarks>
    /// Clearing is safe at any time: a concurrent decode either reads a table it already holds or
    /// rebuilds one, and the tables are immutable, so nothing observes a difference beyond the work.
    /// </remarks>
    internal static void ClearCorrectedTables() => CorrectedTables.Clear();
}
