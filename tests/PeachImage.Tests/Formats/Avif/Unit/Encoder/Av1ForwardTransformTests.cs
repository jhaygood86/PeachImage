using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1ForwardTransform"/> by round-tripping through the existing, already-correct
/// <see cref="Av1InverseTransform.Inverse2D"/>: forward-transform a residual block, place the coefficients
/// into a dequant buffer, inverse-transform it back, and confirm the result reproduces the original
/// residual within the small rounding tolerance the impulse-response construction implies. This is the
/// highest-transcription-risk component of the whole encoder (per the project plan), so it's validated
/// before anything downstream depends on it.
/// </summary>
public class Av1ForwardTransformTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Forward2D_ZeroResidual_ProducesZeroCoefficients(int size)
    {
        int[] residual = new int[size * size];
        int[] coeff = new int[size * size];

        Av1ForwardTransform.Forward2D(residual, coeff, size);

        Assert.All(coeff, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(4, -1)]
    [InlineData(4, 100)]
    [InlineData(8, 50)]
    [InlineData(16, -75)]
    [InlineData(32, 30)]
    public void Forward2D_ConstantResidual_RoundTripsThroughInverse(int size, int constantValue)
    {
        int[] residual = new int[size * size];
        Array.Fill(residual, constantValue);

        AssertRoundTrips(residual, size, tolerance: 2);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Forward2D_RandomResidual_RoundTripsThroughInverseWithinTolerance(int size)
    {
        var random = new Random(12345);
        int[] residual = new int[size * size];
        for (int i = 0; i < residual.Length; i++)
        {
            residual[i] = random.Next(-128, 128);
        }

        AssertRoundTrips(residual, size, tolerance: 3);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Forward2D_SingleImpulseResidual_RoundTripsThroughInverse(int size)
    {
        int[] residual = new int[size * size];
        residual[0] = 200;
        AssertRoundTrips(residual, size, tolerance: 3);

        int[] residualCenter = new int[size * size];
        residualCenter[((size / 2) * size) + (size / 2)] = -150;
        AssertRoundTrips(residualCenter, size, tolerance: 3);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Forward2D_GradientResidual_RoundTripsThroughInverseWithinTolerance(int size)
    {
        int[] residual = new int[size * size];
        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++)
            {
                residual[(row * size) + col] = ((row + col) % 64) - 32;
            }
        }

        AssertRoundTrips(residual, size, tolerance: 3);
    }

    [Fact]
    public void Forward2D_RejectsUnsupportedSize()
    {
        int[] residual = new int[64];
        int[] coeff = new int[64];
        Assert.Throws<ArgumentOutOfRangeException>(() => Av1ForwardTransform.Forward2D(residual, coeff, 64));
    }

    private static void AssertRoundTrips(int[] residual, int size, int tolerance)
    {
        int[] coeff = new int[size * size];
        Av1ForwardTransform.Forward2D(residual, coeff, size);

        int[] dequant = new int[64 * 64];
        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                dequant[(i * 64) + j] = coeff[(i * size) + j];
            }
        }

        int txSz = size switch
        {
            4 => Av1TxSize.Tx4x4,
            8 => Av1TxSize.Tx8x8,
            16 => Av1TxSize.Tx16x16,
            _ => Av1TxSize.Tx32x32,
        };

        int[] reconstructed = new int[size * size];
        Av1InverseTransform.Inverse2D(dequant, reconstructed, txSz, Av1TxType.DctDct, lossless: false, bitDepth: 8);

        for (int i = 0; i < residual.Length; i++)
        {
            Assert.True(
                Math.Abs(reconstructed[i] - residual[i]) <= tolerance,
                $"Index {i}: expected {residual[i]}, got {reconstructed[i]} (size {size}, tolerance {tolerance}).");
        }
    }
}
