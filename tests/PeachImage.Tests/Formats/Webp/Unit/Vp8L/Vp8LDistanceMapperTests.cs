using PeachImage.Formats.Webp.Decoding.Vp8L;
using PeachImage.Formats.Webp.Encoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

/// <summary>Correctness tests for <see cref="Vp8LDistanceMapper"/>, built by inverting the real decode-side <see cref="Vp8LBackwardReferenceTables.PlaneCodeToDistance"/> rather than re-deriving its neighborhood table.</summary>
public class Vp8LDistanceMapperTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(640)]
    [InlineData(1920)]
    public void DistanceToPlaneCode_RoundTrips_ThroughPlaneCodeToDistance(int width)
    {
        var mapper = new Vp8LDistanceMapper(width);

        for (int distance = 1; distance <= 2000; distance++)
        {
            int planeCode = mapper.DistanceToPlaneCode(distance);
            int roundTripped = Vp8LBackwardReferenceTables.PlaneCodeToDistance(width, planeCode);

            Assert.Equal(distance, roundTripped);
        }
    }

    [Fact]
    public void DistanceToPlaneCode_NeverPicksALargerCodeThanTheRawFallback()
    {
        var mapper = new Vp8LDistanceMapper(width: 100);

        for (int distance = 1; distance <= 500; distance++)
        {
            int planeCode = mapper.DistanceToPlaneCode(distance);
            Assert.True(planeCode <= distance + 120);
        }
    }

    [Fact]
    public void DistanceToPlaneCode_FallsBackToRawCode_BeyondTheNeighborhoodTable()
    {
        var mapper = new Vp8LDistanceMapper(width: 4);

        // A distance no short neighborhood code can reach for a narrow width must fall back to the raw
        // (distance + 120) code.
        int planeCode = mapper.DistanceToPlaneCode(1_000_000);
        Assert.Equal(1_000_120, planeCode);
        Assert.Equal(1_000_000, Vp8LBackwardReferenceTables.PlaneCodeToDistance(4, planeCode));
    }
}
