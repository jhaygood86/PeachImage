using System.Runtime.Intrinsics;
using PeachImage.Formats.Avif.Encoder.Av1.Transform;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Vector128Av1MatrixVectorKernel"/>/<see cref="Vector256Av1MatrixVectorKernel"/> agree
/// with <see cref="ScalarAv1MatrixVectorKernel"/> -- the per-kernel-tier counterpart to
/// <see cref="Av1ForwardTransformTests"/>, which only exercises whichever tier the current hardware
/// selected (via <see cref="Av1ForwardTransform"/>'s round-trip-through-the-decoder-inverse tests).
/// </summary>
public class Av1MatrixVectorKernelTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Vector128Apply_MatchesScalarReference(int size)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        AssertAgree(new ScalarAv1MatrixVectorKernel(), new Vector128Av1MatrixVectorKernel(), size);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Vector256Apply_MatchesScalarReference(int size)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("No 256-bit SIMD hardware acceleration (AVX/AVX2) available on this machine.");
        }

        AssertAgree(new ScalarAv1MatrixVectorKernel(), new Vector256Av1MatrixVectorKernel(), size);
    }

    private static void AssertAgree(IAv1MatrixVectorKernel scalar, IAv1MatrixVectorKernel simd, int size)
    {
        var random = new Random(size * 7919);
        var matrix = new double[size, size];
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                matrix[r, c] = (random.NextDouble() * 2000) - 1000;
            }
        }

        var input = new double[size];
        for (int i = 0; i < size; i++)
        {
            input[i] = (random.NextDouble() * 2000) - 1000;
        }

        var scalarOut = new double[size];
        var simdOut = new double[size];
        scalar.Apply(matrix, input, scalarOut, size);
        simd.Apply(matrix, input, simdOut, size);

        for (int i = 0; i < size; i++)
        {
            // Reassociated horizontal summation vs. strictly sequential scalar accumulation -- see
            // Vector256Av1MatrixVectorKernel's remarks. A relative tolerance, since the dot products here
            // can be large in magnitude (up to size * 1000 * 1000).
            double tolerance = Math.Max(1e-6, Math.Abs(scalarOut[i]) * 1e-9);
            Assert.True(
                Math.Abs(scalarOut[i] - simdOut[i]) <= tolerance,
                $"Row {i}: scalar={scalarOut[i]}, simd={simdOut[i]}, tolerance={tolerance}");
        }
    }
}
