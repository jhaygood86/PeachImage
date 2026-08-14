namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// A post-IDCT sample range-limit table (libjpeg-turbo's <c>jdmaster.c</c> <c>prepare_range_limit_table</c>
/// technique), which folds clamp-to-[0,255] into a single masked table load instead of a round call plus a
/// separate compare-based clamp. An inverse-DCT kernel biases its float result by the level shift plus 0.5
/// (round-to-nearest), truncates to <see cref="int"/>, masks with <see cref="Mask"/>, and indexes
/// <see cref="Table"/>.
/// </summary>
/// <remarks>
/// <see cref="Mask"/> covers two bits wider than an 8-bit sample, so the table is exact for every true
/// sample in [-512, 511] — i.e. any 2x overshoot the IDCT of a valid 8-bit JPEG can produce through ringing.
/// A corrupt stream can in principle drive the IDCT further than that; such a value wraps to a
/// bogus-but-in-range byte rather than being clamped correctly. That is deliberate and is exactly the
/// tradeoff libjpeg-turbo documents for the same table: the mask makes an out-of-bounds table index
/// impossible regardless of what the entropy decoder produced, and float-to-int conversion in .NET
/// saturates (never throws or produces an undefined bit pattern) on every platform since .NET Core 3.0, so
/// there is no reachable undefined behavior even on hostile input.
/// </remarks>
internal static class SampleRangeLimit
{
    /// <summary>Mask applied to the biased-and-truncated sample before indexing <see cref="Table"/>.</summary>
    public const int Mask = 1023;

    /// <summary>
    /// 0..255 map to themselves; 256..511 (positive overshoot) saturate to 255; 512..1023 — the masked
    /// image of -512..-1 — saturate to 0.
    /// </summary>
    public static byte[] Table { get; } = Build();

    private static byte[] Build()
    {
        var table = new byte[Mask + 1];
        for (int i = 0; i <= 255; i++)
        {
            table[i] = (byte)i;
        }

        table.AsSpan(256, 256).Fill(255);

        return table;
    }
}
