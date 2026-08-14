using PeachImage.Formats.Jpeg.Decoding.Upsampling;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Upsampling;

/// <summary>
/// Verifies <see cref="TriangleFilterUpsampler"/>'s vectorized <c>horizontalRatio == 2</c> path against an
/// independent reimplementation of the original per-pixel formula (not a call into the production code —
/// see <see cref="ReferenceUpsample"/>), across a range of widths deliberately including non-multiples of 8
/// (the vectorized path processes 8 source columns at a time, so its scalar remainder handling is the part
/// most likely to be wrong) and both real-world ratios, (2,2) and (2,1).
/// </summary>
public class TriangleFilterUpsamplerTests
{
    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    [InlineData(7, 4)]
    [InlineData(8, 4)]
    [InlineData(9, 4)]
    [InlineData(15, 4)]
    [InlineData(16, 4)]
    [InlineData(17, 4)]
    [InlineData(23, 5)]
    [InlineData(64, 6)]
    public void Upsample2x2_MatchesIndependentReference(int sourceWidth, int sourceHeight)
    {
        AssertMatchesReference(sourceWidth, sourceHeight, horizontalRatio: 2, verticalRatio: 2);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(7, 3)]
    [InlineData(8, 3)]
    [InlineData(9, 3)]
    [InlineData(17, 4)]
    public void Upsample2x1_MatchesIndependentReference(int sourceWidth, int sourceHeight)
    {
        AssertMatchesReference(sourceWidth, sourceHeight, horizontalRatio: 2, verticalRatio: 1);
    }

    [Fact]
    public void Upsample_ProducesIdenticalOutput_AcrossManyRandomSeeds()
    {
        var random = new Random(42);
        for (int trial = 0; trial < 50; trial++)
        {
            int width = random.Next(1, 40);
            int height = random.Next(1, 10);
            int vRatio = random.Next(0, 2) == 0 ? 1 : 2;
            AssertMatchesReference(width, height, horizontalRatio: 2, verticalRatio: vRatio, seed: 1000 + trial);
        }
    }

    private static void AssertMatchesReference(int sourceWidth, int sourceHeight, int horizontalRatio, int verticalRatio, int seed = 0)
    {
        var random = new Random(seed ^ (sourceWidth * 397) ^ sourceHeight);
        var source = new byte[sourceWidth * sourceHeight];
        random.NextBytes(source);

        int destinationWidth = sourceWidth * horizontalRatio;
        int destinationHeight = sourceHeight * verticalRatio;

        var actual = new byte[destinationWidth * destinationHeight];
        new TriangleFilterUpsampler().Upsample(source, sourceWidth, sourceHeight, actual, destinationWidth, destinationHeight, horizontalRatio, verticalRatio);

        var expected = ReferenceUpsample(source, sourceWidth, sourceHeight, destinationWidth, destinationHeight, horizontalRatio, verticalRatio);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Independent reimplementation of the 9:3:3:1 triangle-filter formula, transcribed directly from
    /// ITU-T.81-style "fancy" chroma upsampling (the same definition <see cref="TriangleFilterUpsampler"/>'s
    /// original scalar code implements) rather than copied from it — this must not share a bug with the
    /// production code by construction, only by (independently checked) mathematical equivalence.
    /// </summary>
    private static byte[] ReferenceUpsample(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int destinationWidth, int destinationHeight, int horizontalRatio, int verticalRatio)
    {
        var destination = new byte[destinationWidth * destinationHeight];

        for (int y = 0; y < destinationHeight; y++)
        {
            int srcY = Math.Clamp(y / verticalRatio, 0, sourceHeight - 1);
            int srcYNeighbor = verticalRatio == 2
                ? Math.Clamp((y / verticalRatio) + ((y & 1) == 0 ? -1 : 1), 0, sourceHeight - 1)
                : srcY;

            for (int x = 0; x < destinationWidth; x++)
            {
                int srcX = Math.Clamp(x / horizontalRatio, 0, sourceWidth - 1);
                int srcXNeighbor = horizontalRatio == 2
                    ? Math.Clamp((x / horizontalRatio) + ((x & 1) == 0 ? -1 : 1), 0, sourceWidth - 1)
                    : srcX;

                int main = source[(srcY * sourceWidth) + srcX];
                int horizontalNeighbor = source[(srcY * sourceWidth) + srcXNeighbor];
                int verticalNeighbor = source[(srcYNeighbor * sourceWidth) + srcX];
                int diagonalNeighbor = source[(srcYNeighbor * sourceWidth) + srcXNeighbor];

                int blended = ((main * 9) + (horizontalNeighbor * 3) + (verticalNeighbor * 3) + diagonalNeighbor + 8) / 16;
                destination[(y * destinationWidth) + x] = (byte)Math.Clamp(blended, 0, 255);
            }
        }

        return destination;
    }
}
