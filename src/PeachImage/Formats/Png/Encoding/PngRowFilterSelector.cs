using PeachImage.Formats.Png.Filtering;

namespace PeachImage.Formats.Png.Encoding;

/// <summary>Chooses a filter type for one scanline per <see cref="PngFilterStrategy"/> and writes the filtered bytes.</summary>
internal static class PngRowFilterSelector
{
    private static readonly PngFilterType[] AllFilterTypes =
    [
        PngFilterType.None,
        PngFilterType.Sub,
        PngFilterType.Up,
        PngFilterType.Average,
        PngFilterType.Paeth,
    ];

    /// <summary>
    /// <paramref name="scratch"/> is a reusable candidate-scoring buffer of at least <paramref name="raw"/>'s
    /// length, owned by the caller (typically rented once per pass and reused across every row, rather
    /// than rented fresh per row — the Adaptive strategy scores all 5 candidates per call, so avoiding a
    /// rent/return pair per row matters at scale). Ignored for fixed strategies.
    /// </summary>
    public static PngFilterType FilterRow(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> previousRow, int bpp, PngFilterStrategy strategy, Span<byte> scratch, Span<byte> destination)
    {
        if (strategy != PngFilterStrategy.Adaptive)
        {
            var fixedType = ToFilterType(strategy);
            RowFilter.Filter(raw, previousRow, fixedType, bpp, destination);
            return fixedType;
        }

        var scratchSpan = scratch[..raw.Length];
        var bestType = PngFilterType.None;
        long bestScore = long.MaxValue;

        foreach (var candidate in AllFilterTypes)
        {
            RowFilter.Filter(raw, previousRow, candidate, bpp, scratchSpan);
            long score = RowFilter.ScoreMinimumSumOfAbsoluteDifferences(scratchSpan);
            if (score < bestScore)
            {
                bestScore = score;
                bestType = candidate;
                scratchSpan.CopyTo(destination);
            }
        }

        return bestType;
    }

    private static PngFilterType ToFilterType(PngFilterStrategy strategy) => strategy switch
    {
        PngFilterStrategy.FixedNone => PngFilterType.None,
        PngFilterStrategy.FixedSub => PngFilterType.Sub,
        PngFilterStrategy.FixedUp => PngFilterType.Up,
        PngFilterStrategy.FixedAverage => PngFilterType.Average,
        PngFilterStrategy.FixedPaeth => PngFilterType.Paeth,
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, message: null),
    };
}
