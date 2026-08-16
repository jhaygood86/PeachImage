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
    [Theory]
    [MemberData(nameof(ExhaustiveLengthRange))]
    public void EncodePrefixCodeValue_RoundTrips_ThroughDecodePrefixCodeValue(int value)
    {
        AssertRoundTrips(value);
    }

    public static IEnumerable<object[]> ExhaustiveLengthRange()
    {
        for (int v = 1; v <= 5000; v++)
        {
            yield return [v];
        }
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

    private static void AssertRoundTrips(int value)
    {
        var (symbol, extraValue, extraBits) = Vp8LPrefixCodeEncoder.EncodePrefixCodeValue(value);

        var writer = new Vp8LBitWriter();
        writer.WriteBits(extraValue, extraBits);
        byte[] bytes = writer.ToArray();
        var reader = new Vp8LBitReader(bytes, 0, bytes.Length);

        int decoded = Vp8LBackwardReferenceTables.DecodePrefixCodeValue(symbol, reader);

        Assert.Equal(value, decoded);
    }
}
