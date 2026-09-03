using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1ForwardWht"/> round-trips exactly (not just approximately, unlike
/// <see cref="Av1ForwardTransformTests"/>'s DCT case) through the real, unmodified
/// <see cref="Av1InverseTransform.Inverse2D"/> at <c>lossless: true</c> -- lossless has no quantization step
/// to mask rounding error, so anything short of bit-exact would be a real correctness bug, not a tolerable
/// approximation.
/// </summary>
public class Av1ForwardWhtTests
{
    [Fact]
    public void Forward4x4_ZeroResidual_ProducesZeroCoefficients()
    {
        int[] residual = new int[16];
        int[] coeff = new int[16];

        Av1ForwardWht.Forward4x4(residual, coeff);

        Assert.All(coeff, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(-128)]
    [InlineData(127)]
    public void Forward4x4_ConstantResidual_RoundTripsExactly(int constantValue)
    {
        int[] residual = new int[16];
        Array.Fill(residual, constantValue);

        AssertRoundTripsExactly(residual);
    }

    [Fact]
    public void Forward4x4_Gradient_RoundTripsExactly()
    {
        int[] residual = new int[16];
        for (int i = 0; i < 16; i++)
        {
            residual[i] = (i * 17) - 120; // spans negative and positive, not a multiple of 4
        }

        AssertRoundTripsExactly(residual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12345)]
    public void Forward4x4_RandomResiduals_RoundTripExactly(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 200; trial++)
        {
            int[] residual = new int[16];
            for (int i = 0; i < 16; i++)
            {
                residual[i] = random.Next(-255, 256); // a real 8-bit prediction residual's full range
            }

            AssertRoundTripsExactly(residual);
        }
    }

    [Fact]
    public void Forward4x4_SingleNonZeroSample_RoundTripsExactly()
    {
        for (int pos = 0; pos < 16; pos++)
        {
            int[] residual = new int[16];
            residual[pos] = 200;

            AssertRoundTripsExactly(residual);
        }
    }

    private static void AssertRoundTripsExactly(int[] residual)
    {
        int[] coeff = new int[16];
        Av1ForwardWht.Forward4x4(residual, coeff);

        int[] dequant = new int[64 * 64];
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                dequant[(i * 64) + j] = coeff[(i * 4) + j];
            }
        }

        int[] reconstructed = new int[16];

        // planeTxType is ignored by Inverse2D whenever lossless is true (it branches to InverseWht
        // unconditionally), so DctDct here is just a placeholder value, not a meaningful choice.
        Av1InverseTransform.Inverse2D(dequant, reconstructed, Av1TxSize.Tx4x4, Av1TxType.DctDct, lossless: true, bitDepth: 8);

        Assert.Equal(residual, reconstructed);
    }
}
