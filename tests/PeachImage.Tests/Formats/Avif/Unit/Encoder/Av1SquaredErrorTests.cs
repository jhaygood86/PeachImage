using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

public class Av1SquaredErrorTests
{
    [Theory]
    [InlineData(0, 255, 1)]
    [InlineData(1, 255, 7)]
    [InlineData(8, 255, 2)]
    [InlineData(9, 255, 3)]
    [InlineData(31, 4095, 4)]
    [InlineData(32, 4095, 5)]
    [InlineData(33, 4095, 6)]
    [InlineData(1000, 1023, 7)]
    [InlineData(100_003, 4095, 8)]
    public void Vector_MatchesScalar(int length, int maxSample, int seed)
    {
        var rng = new Random(seed);
        var a = new int[length + 3];
        var b = new int[length + 3];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = rng.Next(maxSample + 1);
            b[i] = rng.Next(maxSample + 1);
        }

        // A length shorter than the arrays must ignore the extra samples (CDEF passes the plane length explicitly).
        Assert.Equal(Av1SquaredError.Scalar(a, b, length), Av1SquaredError.Compute(a, b, length));
        if (length >= 8)
        {
            Assert.Equal(Av1SquaredError.Scalar(a, b, length), Av1SquaredError.Vector(a, b, length));
        }
    }

    [Theory]
    [InlineData(0, 4095)]
    [InlineData(4095, 0)]
    public void Vector_IsExactAtTheLargestDifferencesAcrossALongPlane(int fillA, int fillB)
    {
        // 4095^2 per sample over ~9M samples overflows 32 bits many times over; the total must stay exact.
        const int length = 9_000_001;
        var a = new int[length];
        var b = new int[length];
        Array.Fill(a, fillA);
        Array.Fill(b, fillB);
        Assert.Equal(4095L * 4095L * length, Av1SquaredError.Vector(a, b, length));
    }

    [Fact]
    public void Compute_NullPlane_IsZero()
    {
        Assert.Equal(0, Av1SquaredError.Compute(null, new int[16]));
        Assert.Equal(0, Av1SquaredError.Compute(new int[16], null));
        Assert.Equal(0, Av1SquaredError.Compute(null, null, 16));
    }

    [Fact]
    public void Compute_WithoutLength_UsesTheFirstPlanesLength()
    {
        var a = new int[20];
        var b = new int[25];
        Array.Fill(a, 3);
        Array.Fill(b, 1);
        Assert.Equal(20 * 4, Av1SquaredError.Compute(a, b));
    }
}
