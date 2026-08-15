using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Proves the flat probability tables the coefficient decoder actually reads agree with the four-dimensional
/// literals that remain the reviewable source of truth. Exhaustive over all 1056 indices of both tables, which
/// is cheap enough to be total rather than sampled — and total is what's wanted, since a single wrong entry
/// would silently corrupt pixels only for files hitting that one quantizer/plane/band/context combination.
/// </summary>
public class Vp8CoefficientProbabilityFlatteningTests
{
    [Fact]
    public void DefaultFlat_MatchesTheFourDimensionalLiteral() =>
        AssertFlatMatches(Vp8CoefficientProbabilities.Default, Vp8CoefficientProbabilities.DefaultFlat);

    [Fact]
    public void UpdateProbabilityFlat_MatchesTheFourDimensionalLiteral() =>
        AssertFlatMatches(Vp8CoefficientProbabilities.UpdateProbability, Vp8CoefficientProbabilities.UpdateProbabilityFlat);

    private static void AssertFlatMatches(byte[,,,] expected, byte[] flat)
    {
        Assert.Equal(Vp8CoefficientProbabilities.FlatLength, flat.Length);

        for (int t = 0; t < Vp8CoefficientProbabilities.NumTypes; t++)
        {
            for (int b = 0; b < Vp8CoefficientProbabilities.NumBands; b++)
            {
                for (int c = 0; c < Vp8CoefficientProbabilities.NumContexts; c++)
                {
                    int offset = Vp8CoefficientProbabilities.FlatOffset(t, b, c);

                    for (int p = 0; p < Vp8CoefficientProbabilities.NumProbabilities; p++)
                    {
                        Assert.Equal(expected[t, b, c, p], flat[offset + p]);
                    }
                }
            }
        }
    }

    /// <summary>Each (plane type, band, context) triple must map to its own non-overlapping 11-byte run, or the spans handed to the decoder would alias.</summary>
    [Fact]
    public void FlatOffset_IsAContiguousBijectionOverEveryTriple()
    {
        var seen = new HashSet<int>();

        for (int t = 0; t < Vp8CoefficientProbabilities.NumTypes; t++)
        {
            for (int b = 0; b < Vp8CoefficientProbabilities.NumBands; b++)
            {
                for (int c = 0; c < Vp8CoefficientProbabilities.NumContexts; c++)
                {
                    int offset = Vp8CoefficientProbabilities.FlatOffset(t, b, c);

                    Assert.True(offset >= 0 && offset + Vp8CoefficientProbabilities.NumProbabilities <= Vp8CoefficientProbabilities.FlatLength);
                    Assert.True(seen.Add(offset), $"Offset {offset} is produced by more than one (planeType, band, context) triple.");
                }
            }
        }

        Assert.Equal(Vp8CoefficientProbabilities.NumTypes * Vp8CoefficientProbabilities.NumBands * Vp8CoefficientProbabilities.NumContexts, seen.Count);
    }
}
