using System.Runtime.Intrinsics.X86;
using PeachImage.Formats.Jpeg.Dct;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Dct;

/// <summary>
/// Verifies <see cref="AanScalarInverseDct"/>/<see cref="AanScalarForwardDct"/> against the ITU-T.81 A.3.3
/// definition computed directly with <see cref="Math.Cos"/> in this file — independent of
/// <see cref="ScalarInverseDct"/>/<see cref="ScalarForwardDct"/> and of <see cref="AanScaleFactors"/> itself
/// (the expected values below are computed from the closed-form basis function, not by calling
/// <see cref="AanScaleFactors.OneDimensional"/>) — so a bug shared between the AAN kernel and either of
/// those cannot hide from these tests. Unlike <see cref="FastScalarDctIndependentVerificationTests"/>, the
/// AAN kernels' raw output/input carries a per-frequency <c>S(u)*S(v)</c> scale, so every comparison here
/// scales the closed-form expectation (inverse direction) or descales the kernel's output (forward
/// direction) to match, exactly as <see cref="AanScaleFactors"/>' own doc comments specify.
/// </summary>
public class AanScalarDctIndependentVerificationTests
{
    private static double NormalizationFactor(int k) => k == 0 ? 1.0 / Math.Sqrt(2) : 1.0;

    private static double OneDimensionalScaleFactor(int u) => (u == 0 || u == 4) ? 1.0 : Math.Sqrt(2) * Math.Cos(u * Math.PI / 16.0);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void AanScalarInverseDct_SingleRowFrequencyImpulse_MatchesClosedFormBasisFunction(int u)
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
    public void AanScalarInverseDct_SingleColumnFrequencyImpulse_MatchesClosedFormBasisFunction(int v)
    {
        AssertImpulseResponseMatchesClosedForm(u: 0, v, value: 100);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    [InlineData(3, 5)]
    [InlineData(7, 7)]
    public void AanScalarInverseDct_DiagonalFrequencyImpulse_MatchesClosedFormBasisFunction(int u, int v)
    {
        AssertImpulseResponseMatchesClosedForm(u, v, value: 100);
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
    public void Vector256AanInverseDct_SingleRowFrequencyImpulse_MatchesClosedFormBasisFunction(int u)
    {
        if (!Avx.IsSupported)
        {
            Assert.Skip("No AVX hardware acceleration available on this machine.");
        }

        AssertImpulseResponseMatchesClosedForm(u, v: 0, value: 100, new Vector256AanInverseDct());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    [InlineData(3, 5)]
    [InlineData(7, 7)]
    public void Vector256AanInverseDct_DiagonalFrequencyImpulse_MatchesClosedFormBasisFunction(int u, int v)
    {
        if (!Avx.IsSupported)
        {
            Assert.Skip("No AVX hardware acceleration available on this machine.");
        }

        AssertImpulseResponseMatchesClosedForm(u, v, value: 100, new Vector256AanInverseDct());
    }

    private static void AssertImpulseResponseMatchesClosedForm(int u, int v, short value, IInverseDctKernel? kernel = null)
    {
        kernel ??= new AanScalarInverseDct();

        Span<short> coefficients = stackalloc short[64];
        coefficients[(v * 8) + u] = value;
        Span<ushort> quant = stackalloc ushort[64];
        quant.Fill(1);

        var dequant = kernel.PrepareDequantTable(quant);

        Span<byte> output = stackalloc byte[64];
        kernel.Transform(coefficients, dequant, output, outputStride: 8);

        // Unlike the forward direction, the S(u)*S(v) scale is fully absorbed by PrepareDequantTable
        // (applied to the input before the network even runs) combined with the network's own internal
        // gain, so — by design, per AanScaleFactors' contract — the kernel's output should match the plain
        // closed-form basis function directly, with no further scaling in this expectation.
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                double raw = 0.25 * NormalizationFactor(u) * NormalizationFactor(v) * value
                    * Math.Cos(((2 * x) + 1) * u * Math.PI / 16.0)
                    * Math.Cos(((2 * y) + 1) * v * Math.PI / 16.0);
                double expectedPixel = Math.Clamp(raw + 128.0, 0, 255);
                Assert.True(Math.Abs(expectedPixel - output[(y * 8) + x]) <= 1,
                    $"(x={x},y={y}): expected ~{expectedPixel:F3}, got {output[(y * 8) + x]}");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AanScalarForwardDct_MatchesClosedFormDefinition_ForRandomBlocks(int seed)
    {
        AssertForwardMatchesClosedForm(new AanScalarForwardDct(), seed, tolerance: 1e-6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Vector256AanForwardDct_MatchesClosedFormDefinition_ForRandomBlocks(int seed)
    {
        if (!Avx.IsSupported)
        {
            Assert.Skip("No AVX hardware acceleration available on this machine.");
        }

        AssertForwardMatchesClosedForm(new Vector256AanForwardDct(), seed, tolerance: 1.0);
    }

    private static void AssertForwardMatchesClosedForm(IForwardDctKernel kernel, int seed, double tolerance)
    {
        var random = new Random(seed);
        Span<byte> input = stackalloc byte[64];
        for (int i = 0; i < 64; i++)
        {
            input[i] = (byte)random.Next(0, 256);
        }

        Span<double> rawOutput = stackalloc double[64];
        kernel.Transform(input, inputStride: 8, rawOutput);

        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                double expected = TrueForwardCoefficient(input, inputStride: 8, u, v);
                double scale = OneDimensionalScaleFactor(u) * OneDimensionalScaleFactor(v);
                double actual = rawOutput[(v * 8) + u] / scale;
                Assert.True(Math.Abs(expected - actual) <= tolerance, $"(u={u},v={v}): expected {expected:F6}, got {actual:F6}");
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
