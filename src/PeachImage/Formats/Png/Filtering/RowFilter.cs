using PeachImage.Formats.Png;

namespace PeachImage.Formats.Png.Filtering;

/// <summary>
/// PNG row filtering/unfiltering (spec §6). Operates on still bit-depth-packed scanline bytes, before
/// bit-depth unpacking. <see cref="Filter"/> (encode) has no cross-byte dependency for any of the 5
/// filter types — the raw pixel row is already fully known — so all 5 dispatch to
/// <see cref="VectorizedRowFilter"/> when worthwhile. <see cref="Unfilter"/> (decode) has a genuine
/// same-row sequential dependency for Sub/Average/Paeth (each byte's reconstruction reads the
/// just-reconstructed <c>recon[x-bpp]</c>): <see cref="PngFilterType.Up"/> has no such dependency at all
/// (it only reads the already-fully-resolved previous row) and is fully vectorized; Average/Paeth are
/// additionally nonlinear recurrences that don't reduce to <see cref="PngFilterType.Sub"/>'s parallel-prefix
/// pattern, but are vectorized at the per-pixel-step granularity (see
/// <see cref="VectorizedRowFilter.UnfilterAverage"/>/<see cref="VectorizedRowFilter.UnfilterPaeth"/>) —
/// issue #34: profiling found Sub is ~0% of real decode time across this repo's benchmark corpus (real
/// encoders rarely pick it for photographic content) while Average/Paeth are 13%–79%, so Sub decode
/// remains scalar-only as not worth the risk, while Average/Paeth's per-pixel-step vectorization measured
/// as a real win at every <c>bpp</c> tested (1 through 8), not just larger multi-channel/16-bit ones.
/// </summary>
internal static class RowFilter
{
    /// <summary>Reconstructs <paramref name="row"/> in place from its filtered form.</summary>
    /// <param name="row">The filtered scanline bytes (mutated in place to become the reconstructed bytes).</param>
    /// <param name="previousRow">The previous scanline's already-reconstructed bytes, or empty for the first row of a pass.</param>
    /// <param name="filterType">The filter type this row was encoded with.</param>
    /// <param name="bpp">The filter distance-back, in bytes (<see cref="Internal.PngHeader.FilterBytesPerPixel"/>).</param>
    public static void Unfilter(Span<byte> row, ReadOnlySpan<byte> previousRow, PngFilterType filterType, int bpp)
    {
        switch (filterType)
        {
            case PngFilterType.None:
                return;

            case PngFilterType.Sub:
                for (int x = bpp; x < row.Length; x++)
                {
                    row[x] = (byte)(row[x] + row[x - bpp]);
                }

                return;

            case PngFilterType.Up:
                if (previousRow.IsEmpty)
                {
                    return;
                }

                if (VectorizedRowFilter.IsWorthwhile(row.Length))
                {
                    VectorizedRowFilter.UnfilterUp(row, previousRow);
                    return;
                }

                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = (byte)(row[x] + previousRow[x]);
                }

                return;

            case PngFilterType.Average:
                if (VectorizedRowFilter.IsWorthwhile(row.Length))
                {
                    VectorizedRowFilter.UnfilterAverage(row, previousRow, bpp);
                    return;
                }

                for (int x = 0; x < row.Length; x++)
                {
                    int a = x >= bpp ? row[x - bpp] : 0;
                    int b = previousRow.IsEmpty ? 0 : previousRow[x];
                    row[x] = (byte)(row[x] + ((a + b) / 2));
                }

                return;

            case PngFilterType.Paeth:
                if (VectorizedRowFilter.IsWorthwhile(row.Length))
                {
                    VectorizedRowFilter.UnfilterPaeth(row, previousRow, bpp);
                    return;
                }

                for (int x = 0; x < row.Length; x++)
                {
                    byte a = x >= bpp ? row[x - bpp] : (byte)0;
                    byte b = previousRow.IsEmpty ? (byte)0 : previousRow[x];
                    byte c = (x >= bpp && !previousRow.IsEmpty) ? previousRow[x - bpp] : (byte)0;
                    row[x] = (byte)(row[x] + PaethPredictor.Predict(a, b, c));
                }

                return;

            default:
                throw new PngDecodingException($"Unsupported PNG filter type {(byte)filterType}.");
        }
    }

    /// <summary>Filters <paramref name="raw"/> (unfiltered scanline bytes) into <paramref name="destination"/>.</summary>
    public static void Filter(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> previousRow, PngFilterType filterType, int bpp, Span<byte> destination)
    {
        bool vectorize = VectorizedRowFilter.IsWorthwhile(raw.Length);

        switch (filterType)
        {
            case PngFilterType.None:
                raw.CopyTo(destination);
                return;

            case PngFilterType.Sub:
                if (vectorize)
                {
                    VectorizedRowFilter.FilterSub(raw, bpp, destination);
                    return;
                }

                for (int x = 0; x < raw.Length; x++)
                {
                    byte a = x >= bpp ? raw[x - bpp] : (byte)0;
                    destination[x] = (byte)(raw[x] - a);
                }

                return;

            case PngFilterType.Up:
                if (previousRow.IsEmpty)
                {
                    raw.CopyTo(destination);
                    return;
                }

                if (vectorize)
                {
                    VectorizedRowFilter.FilterUp(raw, previousRow, destination);
                    return;
                }

                for (int x = 0; x < raw.Length; x++)
                {
                    destination[x] = (byte)(raw[x] - previousRow[x]);
                }

                return;

            case PngFilterType.Average:
                if (vectorize)
                {
                    VectorizedRowFilter.FilterAverage(raw, previousRow, bpp, destination);
                    return;
                }

                for (int x = 0; x < raw.Length; x++)
                {
                    int a = x >= bpp ? raw[x - bpp] : 0;
                    int b = previousRow.IsEmpty ? 0 : previousRow[x];
                    destination[x] = (byte)(raw[x] - ((a + b) / 2));
                }

                return;

            case PngFilterType.Paeth:
                if (vectorize)
                {
                    VectorizedRowFilter.FilterPaeth(raw, previousRow, bpp, destination);
                    return;
                }

                for (int x = 0; x < raw.Length; x++)
                {
                    byte a = x >= bpp ? raw[x - bpp] : (byte)0;
                    byte b = previousRow.IsEmpty ? (byte)0 : previousRow[x];
                    byte c = (x >= bpp && !previousRow.IsEmpty) ? previousRow[x - bpp] : (byte)0;
                    destination[x] = (byte)(raw[x] - PaethPredictor.Predict(a, b, c));
                }

                return;

            default:
                throw new PngEncodingException($"Unsupported PNG filter type {(byte)filterType}.");
        }
    }

    /// <summary>Scores a filtered candidate row via libpng's minimum-sum-of-absolute-differences heuristic (lower is better).</summary>
    public static long ScoreMinimumSumOfAbsoluteDifferences(ReadOnlySpan<byte> filtered)
    {
        long sum = 0;
        foreach (byte b in filtered)
        {
            sum += b < 128 ? b : 256 - b;
        }

        return sum;
    }
}
