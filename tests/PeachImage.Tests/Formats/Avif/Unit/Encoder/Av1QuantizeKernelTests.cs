using System.Runtime.Intrinsics;
using PeachImage.Formats.Avif.Encoder.Av1.Quantization;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Vector128Av1QuantizeKernel"/>/<see cref="Vector256Av1QuantizeKernel"/> agree exactly
/// with <see cref="ScalarAv1QuantizeKernel"/> -- the manual away-from-zero rounding
/// (<see cref="Vector256Av1QuantizeKernel"/>'s <c>RoundAwayFromZero</c>) and the exact int32-&lt;-&gt;double
/// widen/narrow chain give no room for tolerance here, unlike <see cref="Av1RgbToYuvKernelTests"/> and
/// <see cref="Av1MatrixVectorKernelTests"/> (which reassociate floating-point sums).
/// </summary>
public class Av1QuantizeKernelTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Vector128Quantize_MatchesScalarReferenceExactly(int size)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        AssertAgree(new ScalarAv1QuantizeKernel(), new Vector128Av1QuantizeKernel(), size);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Vector256Quantize_MatchesScalarReferenceExactly(int size)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("No 256-bit SIMD hardware acceleration (AVX/AVX2) available on this machine.");
        }

        AssertAgree(new ScalarAv1QuantizeKernel(), new Vector256Av1QuantizeKernel(), size);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Quantize_RoundsHalfwayValuesAwayFromZero(int size)
    {
        // dcReciprocal/acReciprocal both 0.5 puts every non-zero even coefficient exactly on a
        // rounding-midpoint boundary -- the case MidpointRounding.AwayFromZero and round-half-to-even
        // actually disagree on, so this specifically pins the away-from-zero convention (not just "close
        // enough" tolerance) for both the scalar reference and whichever SIMD tier the current hardware
        // selects.
        int total = size * size;
        var coeff = new int[total];
        for (int i = 0; i < total; i++)
        {
            coeff[i] = (i % 2 == 0) ? 3 : -3; // 3 * 0.5 = 1.5, -3 * 0.5 = -1.5
        }

        var levels = new int[total];
        Av1QuantizeKernelSelector.Instance.Quantize(coeff, levels, size, dcReciprocal: 0.5, acReciprocal: 0.5);

        for (int i = 0; i < total; i++)
        {
            int expected = (i % 2 == 0) ? 2 : -2; // away from zero: 1.5 -> 2, -1.5 -> -2
            Assert.Equal(expected, levels[i]);
        }
    }

    private static void AssertAgree(IAv1QuantizeKernel scalar, IAv1QuantizeKernel simd, int size)
    {
        int total = size * size;
        var random = new Random(size * 104729);
        var coeff = new int[total];
        for (int i = 0; i < total; i++)
        {
            coeff[i] = random.Next(-4096, 4096);
        }

        const double dcReciprocal = 1.0 / 17.0;
        const double acReciprocal = 1.0 / 53.0;

        var scalarOut = new int[total];
        var simdOut = new int[total];
        scalar.Quantize(coeff, scalarOut, size, dcReciprocal, acReciprocal);
        simd.Quantize(coeff, simdOut, size, dcReciprocal, acReciprocal);

        Assert.Equal(scalarOut, simdOut);
    }
}
