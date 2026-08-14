using PeachImage.Formats.Jpeg.Dct;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Dct;

/// <summary>
/// Verifies <see cref="FastScalarInverseDct"/>/<see cref="FastScalarForwardDct"/> against the ITU-T.81
/// A.3.3 definition computed directly with <see cref="Math.Cos"/> in this file — independent of
/// <see cref="ScalarInverseDct"/>/<see cref="ScalarForwardDct"/> — so a bug shared between the shipped fast
/// kernel and this repo's own scalar reference cannot hide from these tests.
/// </summary>
public class FastScalarDctIndependentVerificationTests
{
    private static double NormalizationFactor(int k) => k == 0 ? 1.0 / Math.Sqrt(2) : 1.0;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void FastScalarInverseDct_SingleRowFrequencyImpulse_MatchesClosedFormBasisFunction(int u)
    {
        AssertImpulseResponseMatchesClosedForm(u, v: 0, value: 100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void FastScalarInverseDct_SingleColumnFrequencyImpulse_MatchesClosedFormBasisFunction(int v)
    {
        AssertImpulseResponseMatchesClosedForm(u: 0, v, value: 100);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    [InlineData(3, 5)]
    [InlineData(7, 7)]
    public void FastScalarInverseDct_DiagonalFrequencyImpulse_MatchesClosedFormBasisFunction(int u, int v)
    {
        AssertImpulseResponseMatchesClosedForm(u, v, value: 100);
    }

    private static void AssertImpulseResponseMatchesClosedForm(int u, int v, short value)
    {
        Span<short> coefficients = stackalloc short[64];
        coefficients[(v * 8) + u] = value;
        Span<float> dequant = stackalloc float[64];
        dequant.Fill(1f);

        Span<byte> output = stackalloc byte[64];
        new FastScalarInverseDct().Transform(coefficients, dequant, output, outputStride: 8);

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                double raw = 0.25 * NormalizationFactor(u) * NormalizationFactor(v) * value
                    * Math.Cos(((2 * x) + 1) * u * Math.PI / 16.0)
                    * Math.Cos(((2 * y) + 1) * v * Math.PI / 16.0);
                double expectedPixel = Math.Clamp(raw + 128.0, 0, 255);
                // A tolerance of 1, not exact equality: some (u,v,x,y) combinations land the true value
                // exactly on a .5 rounding boundary, where this test's independently-computed cosine
                // product and the kernel's differently-ordered arithmetic can legitimately round to
                // adjacent integers despite agreeing to double precision beforehand.
                Assert.True(Math.Abs(expectedPixel - output[(y * 8) + x]) <= 1,
                    $"(x={x},y={y}): expected ~{expectedPixel:F3}, got {output[(y * 8) + x]}");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FastScalarForwardDct_MatchesClosedFormDefinition_ForRandomBlocks(int seed)
    {
        var random = new Random(seed);
        Span<byte> input = stackalloc byte[64];
        for (int i = 0; i < 64; i++)
        {
            input[i] = (byte)random.Next(0, 256);
        }

        Span<double> output = stackalloc double[64];
        new FastScalarForwardDct().Transform(input, inputStride: 8, output);

        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                double expected = TrueForwardCoefficient(input, inputStride: 8, u, v);
                double actual = output[(v * 8) + u];
                Assert.True(Math.Abs(expected - actual) <= 1e-6, $"(u={u},v={v}): expected {expected:F6}, got {actual:F6}");
            }
        }
    }

    /// <summary>
    /// The ITU-T.81 A.3.3 forward DCT sum, computed directly here (not via any production kernel).
    /// </summary>
    private static double TrueForwardCoefficient(ReadOnlySpan<byte> input, int inputStride, int u, int v)
    {
        double sum = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                double shifted = input[(y * inputStride) + x] - 128.0;
                sum += shifted
                    * Math.Cos(((2 * x) + 1) * u * Math.PI / 16.0)
                    * Math.Cos(((2 * y) + 1) * v * Math.PI / 16.0);
            }
        }

        return 0.25 * NormalizationFactor(u) * NormalizationFactor(v) * sum;
    }
}
