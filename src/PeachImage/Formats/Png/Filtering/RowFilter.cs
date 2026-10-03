using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
        int i = 0;

        if (Vector128.IsHardwareAccelerated && filtered.Length >= Vector128<byte>.Count)
        {
            // |b| for b read as a signed byte is min(b, 256 - b) as an unsigned byte (0 -> 0, 128 -> 128), so
            // min(v, 0 - v) with byte wraparound scores 16 bytes at once. Each byte scores at most 128, so a
            // ushort lane (two bytes widened and added per step, at most 256) holds 255 steps before it could
            // overflow; flush into uint lanes well before that.
            const int FlushEvery = 128;
            var total = Vector128<uint>.Zero;
            var zero = Vector128<byte>.Zero;
            int vectorEnd = filtered.Length - Vector128<byte>.Count;
            ref byte start = ref MemoryMarshal.GetReference(filtered);

            while (i <= vectorEnd)
            {
                var partial = Vector128<ushort>.Zero;
                for (int step = 0; step < FlushEvery && i <= vectorEnd; step++, i += Vector128<byte>.Count)
                {
                    var v = Vector128.LoadUnsafe(ref start, (nuint)i);
                    var scored = Vector128.Min(v, zero - v);
                    partial += Vector128.WidenLower(scored) + Vector128.WidenUpper(scored);
                }

                total += Vector128.WidenLower(partial) + Vector128.WidenUpper(partial);
            }

            sum = Vector128.Sum(total);
        }

        for (; i < filtered.Length; i++)
        {
            byte b = filtered[i];
            sum += b < 128 ? b : 256 - b;
        }

        return sum;
    }
}
