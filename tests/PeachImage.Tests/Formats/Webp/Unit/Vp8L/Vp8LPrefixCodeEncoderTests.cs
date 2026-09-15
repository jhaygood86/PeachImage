using System.Collections.Concurrent;
using PeachImage.Formats.Webp.Decoding.Vp8L;
using PeachImage.Formats.Webp.Encoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

/// <summary>
/// Correctness tests for <see cref="Vp8LPrefixCodeEncoder"/> — the inverse of
/// <see cref="Vp8LBackwardReferenceTables.DecodePrefixCodeValue"/>. Round-trips every encoded value through
/// the real decode-side function to confirm the two agree.
/// </summary>
public class Vp8LPrefixCodeEncoderTests
{
    /// <summary>
    /// Exhaustively round-trips every value from 1 to 5000 in one aggregate test rather than one xUnit
    /// <c>[Theory]</c> case per value: 5,000 individual cases all land in this class's single collection (xUnit
    /// parallelizes across collections, not within one), so they'd run serially and pay per-case
    /// discovery/reporting overhead 5,000 times over. <see cref="Parallel.For(int,int,Action{int})"/> keeps the
    /// exhaustive coverage while actually using more than one core, since each value's round trip is
    /// independent and side-effect-free.
    /// </summary>
    [Fact]
    public void EncodePrefixCodeValue_RoundTrips_ThroughDecodePrefixCodeValue_Exhaustive()
    {
        var failures = new ConcurrentBag<int>();

        Parallel.For(1, 5001, v =>
        {
            if (!RoundTrips(v))
            {
                failures.Add(v);
            }
        });

        Assert.True(failures.IsEmpty, $"Round trip failed for value(s): {string.Join(", ", failures.OrderBy(v => v))}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(4096)] // MaxBackwardReferenceLength.
    [InlineData(100_000)]
    [InlineData(500_000)]
    [InlineData(1_048_576)] // MaxBackwardReferenceDistance.
    public void EncodePrefixCodeValue_RoundTrips_AtDistanceScaleBoundaries(int value)
    {
        AssertRoundTrips(value);
    }

    private static void AssertRoundTrips(int value) => Assert.True(RoundTrips(value));

    private static bool RoundTrips(int value)
    {
        var (symbol, extraValue, extraBits) = Vp8LPrefixCodeEncoder.EncodePrefixCodeValue(value);

        var writer = new Vp8LBitWriter();
        writer.WriteBits(extraValue, extraBits);
        byte[] bytes = writer.ToArray();
        var reader = new Vp8LBitReader(bytes, 0, bytes.Length);

        int decoded = Vp8LBackwardReferenceTables.DecodePrefixCodeValue(symbol, reader);

        return decoded == value;
    }
}
