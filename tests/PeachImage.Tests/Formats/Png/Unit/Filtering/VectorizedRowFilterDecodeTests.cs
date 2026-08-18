using PeachImage.Formats.Png.Filtering;

namespace PeachImage.Tests.Formats.Png.Unit.Filtering;

/// <summary>
/// Randomized cross-tier equivalence tests for the decode-side per-pixel-step vectorized kernels added
/// for issue #34 (<see cref="VectorizedRowFilter.UnfilterAverage"/>/<see cref="VectorizedRowFilter.UnfilterPaeth"/>):
/// compares their output against a scalar reference implementation matching the pre-vectorization
/// <c>RowFilter.Unfilter</c> loops, across every realistic <c>bpp</c> value, both with and without a
/// previous row, and lengths spanning the SIMD-width/pixel-step boundaries (short of one pixel, exactly
/// one vector step, one vector step plus a partial scalar tail, several vector steps, large/realistic
/// row widths).
/// </summary>
public class VectorizedRowFilterDecodeTests
{
    public static IEnumerable<object[]> BppValues() => [[1], [2], [3], [4], [6], [8]];

    public static IEnumerable<object[]> BppAndLengthCases()
    {
        foreach (int bpp in new[] { 1, 2, 3, 4, 6, 8 })
        {
            foreach (int length in LengthsFor(bpp))
            {
                yield return [bpp, length];
            }
        }
    }

    private static IEnumerable<int> LengthsFor(int bpp)
    {
        yield return bpp; // exactly one pixel, no vector step at all.
        yield return bpp * 2;
        yield return bpp * 3;
        yield return 32 + bpp; // roughly one vector step (up to AVX2 width) plus one pixel.
        yield return 32 + bpp * 5; // one vector step plus a multi-pixel scalar tail.
        yield return 64 * bpp; // several vector steps, exact multiple of bpp.
        yield return 64 * bpp + 1; // several vector steps plus a 1-byte scalar tail (only reachable when bpp==1).
        yield return 1920 * 3; // realistic 1080p-width RGB row.
        yield return 1920 * 8; // realistic 1080p-width Rgba64 row.
    }

    [Theory]
    [MemberData(nameof(BppAndLengthCases))]
    public void UnfilterAverage_MatchesScalarReference_WithPreviousRow(int bpp, int length)
    {
        AssertAverageMatches(bpp, length, hasPreviousRow: true, seed: (uint)(bpp * 1000 + length));
    }

    [Theory]
    [MemberData(nameof(BppAndLengthCases))]
    public void UnfilterAverage_MatchesScalarReference_FirstRow(int bpp, int length)
    {
        AssertAverageMatches(bpp, length, hasPreviousRow: false, seed: (uint)(bpp * 2000 + length));
    }

    [Theory]
    [MemberData(nameof(BppAndLengthCases))]
    public void UnfilterPaeth_MatchesScalarReference_WithPreviousRow(int bpp, int length)
    {
        AssertPaethMatches(bpp, length, hasPreviousRow: true, seed: (uint)(bpp * 3000 + length));
    }

    [Theory]
    [MemberData(nameof(BppAndLengthCases))]
    public void UnfilterPaeth_MatchesScalarReference_FirstRow(int bpp, int length)
    {
        AssertPaethMatches(bpp, length, hasPreviousRow: false, seed: (uint)(bpp * 4000 + length));
    }

    /// <summary>Edge-case byte values (0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF repeating) specifically to stress Average/Paeth's wraparound and sign-boundary behavior, mirroring <c>VectorizedFilterRoundTripTests.EdgeCaseByteValues_RoundTripExactly</c>.</summary>
    [Theory]
    [MemberData(nameof(BppValues))]
    public void UnfilterAverage_EdgeCaseByteValues_MatchesScalarReference(int bpp)
    {
        int length = 64 * bpp + bpp / 2 + 1;
        byte[] filtered = EdgeCaseBytes(length);
        byte[] previousRow = EdgeCaseBytes(length, offset: 3);

        AssertAverageEquivalent(filtered, previousRow, bpp);
    }

    [Theory]
    [MemberData(nameof(BppValues))]
    public void UnfilterPaeth_EdgeCaseByteValues_MatchesScalarReference(int bpp)
    {
        int length = 64 * bpp + bpp / 2 + 1;
        byte[] filtered = EdgeCaseBytes(length);
        byte[] previousRow = EdgeCaseBytes(length, offset: 3);

        AssertPaethEquivalent(filtered, previousRow, bpp);
    }

    private static byte[] EdgeCaseBytes(int length, int offset = 0)
    {
        byte[] edgeValues = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF];
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = edgeValues[(i + offset) % edgeValues.Length];
        }

        return bytes;
    }

    private static void AssertAverageMatches(int bpp, int length, bool hasPreviousRow, uint seed)
    {
        byte[] filtered = RandomBytes(length, seed);
        byte[]? previousRow = hasPreviousRow ? RandomBytes(length, seed ^ 0xA5A5A5A5) : null;

        AssertAverageEquivalent(filtered, previousRow, bpp);
    }

    private static void AssertPaethMatches(int bpp, int length, bool hasPreviousRow, uint seed)
    {
        byte[] filtered = RandomBytes(length, seed);
        byte[]? previousRow = hasPreviousRow ? RandomBytes(length, seed ^ 0x5A5A5A5A) : null;

        AssertPaethEquivalent(filtered, previousRow, bpp);
    }

    private static void AssertAverageEquivalent(byte[] filtered, byte[]? previousRow, int bpp)
    {
        byte[] expected = (byte[])filtered.Clone();
        UnfilterAverageScalarReference(expected, previousRow, bpp);

        byte[] actual = (byte[])filtered.Clone();
        VectorizedRowFilter.UnfilterAverage(actual, previousRow, bpp);

        Assert.Equal(expected, actual);
    }

    private static void AssertPaethEquivalent(byte[] filtered, byte[]? previousRow, int bpp)
    {
        byte[] expected = (byte[])filtered.Clone();
        UnfilterPaethScalarReference(expected, previousRow, bpp);

        byte[] actual = (byte[])filtered.Clone();
        VectorizedRowFilter.UnfilterPaeth(actual, previousRow, bpp);

        Assert.Equal(expected, actual);
    }

    /// <summary>Matches the scalar loop <c>RowFilter.Unfilter</c> used for <see cref="PngFilterType.Average"/> before issue #34's vectorization.</summary>
    private static void UnfilterAverageScalarReference(Span<byte> row, ReadOnlySpan<byte> previousRow, int bpp)
    {
        for (int x = 0; x < row.Length; x++)
        {
            int a = x >= bpp ? row[x - bpp] : 0;
            int b = previousRow.IsEmpty ? 0 : previousRow[x];
            row[x] = (byte)(row[x] + ((a + b) / 2));
        }
    }

    /// <summary>Matches the scalar loop <c>RowFilter.Unfilter</c> used for <see cref="PngFilterType.Paeth"/> before issue #34's vectorization.</summary>
    private static void UnfilterPaethScalarReference(Span<byte> row, ReadOnlySpan<byte> previousRow, int bpp)
    {
        for (int x = 0; x < row.Length; x++)
        {
            byte a = x >= bpp ? row[x - bpp] : (byte)0;
            byte b = previousRow.IsEmpty ? (byte)0 : previousRow[x];
            byte c = (x >= bpp && !previousRow.IsEmpty) ? previousRow[x - bpp] : (byte)0;
            row[x] = (byte)(row[x] + PaethPredictor.Predict(a, b, c));
        }
    }

    /// <summary>Deterministic pseudo-random byte stream (xorshift, not <see cref="Random"/>) covering the full byte range reproducibly across runtimes.</summary>
    private static byte[] RandomBytes(int length, uint seed)
    {
        uint state = seed == 0 ? 0x9E3779B9u : seed;
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[i] = (byte)state;
        }

        return bytes;
    }
}
