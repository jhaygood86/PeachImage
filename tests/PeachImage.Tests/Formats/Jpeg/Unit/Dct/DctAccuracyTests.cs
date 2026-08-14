using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PeachImage.Formats.Jpeg.Dct;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Dct;

public class DctAccuracyTests
{
    [Fact]
    public void InverseDct_OfZeroCoefficients_ProducesFlatMidGrayBlock()
    {
        Span<short> coefficients = stackalloc short[64];
        Span<ushort> quant = stackalloc ushort[64];
        quant.Fill(1);

        Span<byte> output = stackalloc byte[64];
        IInverseDctKernel kernel = new ScalarInverseDct();
        kernel.Transform(coefficients, kernel.PrepareDequantTable(quant), output, outputStride: 8);

        foreach (byte b in output)
        {
            Assert.Equal(128, b);
        }
    }

    [Fact]
    public void InverseDct_OfDcOnlyCoefficient_ProducesUniformBlock()
    {
        Span<short> coefficients = stackalloc short[64];
        coefficients[0] = 8; // DC coefficient chosen so the mean shift is a whole, easily-checked number
        Span<ushort> quant = stackalloc ushort[64];
        quant.Fill(1);

        Span<byte> output = stackalloc byte[64];
        IInverseDctKernel kernel = new ScalarInverseDct();
        kernel.Transform(coefficients, kernel.PrepareDequantTable(quant), output, outputStride: 8);

        // DC-only IDCT of value V: C(0)=1/sqrt(2), so the shift is C(0)^2 * (1/4) * V = V/8.
        byte expected = (byte)(128 + (8 / 8));
        foreach (byte b in output)
        {
            Assert.Equal(expected, b);
        }
    }

    [Fact]
    public void ForwardThenInverseDct_RoundTripsExactlyForConstantBlock()
    {
        Span<byte> input = stackalloc byte[64];
        input.Fill(200);

        Span<double> coefficients = stackalloc double[64];
        new ScalarForwardDct().Transform(input, inputStride: 8, coefficients);

        Span<short> rounded = stackalloc short[64];
        for (int i = 0; i < 64; i++)
        {
            rounded[i] = (short)Math.Round(coefficients[i]);
        }

        Span<ushort> quant = stackalloc ushort[64];
        quant.Fill(1);

        Span<byte> output = stackalloc byte[64];
        IInverseDctKernel kernel = new ScalarInverseDct();
        kernel.Transform(rounded, kernel.PrepareDequantTable(quant), output, outputStride: 8);

        foreach (byte b in output)
        {
            Assert.Equal(200, b);
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(90)]
    public void ForwardThenInverseDct_RoundTripsWithinToleranceForRandomBlocks(int seed)
    {
        var random = new Random(seed);
        Span<byte> input = stackalloc byte[64];
        for (int i = 0; i < 64; i++)
        {
            input[i] = (byte)random.Next(0, 256);
        }

        Span<double> coefficients = stackalloc double[64];
        new ScalarForwardDct().Transform(input, inputStride: 8, coefficients);

        Span<short> rounded = stackalloc short[64];
        for (int i = 0; i < 64; i++)
        {
            rounded[i] = (short)Math.Round(coefficients[i]);
        }

        Span<ushort> quant = stackalloc ushort[64];
        quant.Fill(1);

        Span<byte> output = stackalloc byte[64];
        IInverseDctKernel kernel = new ScalarInverseDct();
        kernel.Transform(rounded, kernel.PrepareDequantTable(quant), output, outputStride: 8);

        for (int i = 0; i < 64; i++)
        {
            Assert.True(Math.Abs(input[i] - output[i]) <= 2, $"Sample {i}: expected ~{input[i]}, got {output[i]}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Vector128InverseDct_MatchesScalarReference_ForRandomBlocks(int seed)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        AssertInverseDctKernelsAgree(new ScalarInverseDct(), new Vector128InverseDct(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Vector256InverseDct_MatchesScalarReference_ForRandomBlocks(int seed)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("No 256-bit SIMD hardware acceleration (AVX/AVX2) available on this machine.");
        }

        AssertInverseDctKernelsAgree(new ScalarInverseDct(), new Vector256InverseDct(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Vector128ForwardDct_MatchesScalarReference_ForRandomBlocks(int seed)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        AssertForwardDctKernelsAgree(new ScalarForwardDct(), new Vector128ForwardDct(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Vector256ForwardDct_MatchesScalarReference_ForRandomBlocks(int seed)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("No 256-bit SIMD hardware acceleration (AVX/AVX2) available on this machine.");
        }

        AssertForwardDctKernelsAgree(new ScalarForwardDct(), new Vector256ForwardDct(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void FastScalarInverseDct_MatchesScalarReference_ForRandomBlocks(int seed)
    {
        // FastScalarInverseDct is exact (same double-precision math, just reordered), so it should agree
        // with the direct-definition reference far more tightly than the float32 SIMD kernels do.
        AssertInverseDctKernelsAgree(new ScalarInverseDct(), new FastScalarInverseDct(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FastScalarForwardDct_MatchesScalarReference_ForRandomBlocks(int seed)
    {
        AssertForwardDctKernelsAgree(new ScalarForwardDct(), new FastScalarForwardDct(), seed, tolerance: 1e-6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AanScalarInverseDct_MatchesScalarReference_ForRandomBlocks(int seed)
    {
        // AanScalarInverseDct's PrepareDequantTable folds S(u)*S(v) into the dequant table it hands back,
        // so this reuses the same generic comparison as the other tiers: each kernel prepares its own
        // dequant table from the same raw quant values, and the resulting pixels should still agree.
        AssertInverseDctKernelsAgree(new ScalarInverseDct(), new AanScalarInverseDct(), seed);
    }

    [Theory]
    [InlineData(2976, 255)] // 128 + 2976/8 = 500 -> saturates high
    [InlineData(-2624, 0)] // 128 + -2624/8 = -200 -> saturates low
    [InlineData(1016, 255)] // 128 + 1016/8 = 255 -> exactly the high boundary, no saturation needed
    [InlineData(-1024, 0)] // 128 + -1024/8 = 0 -> exactly the low boundary, no saturation needed
    public void AanScalarInverseDct_OfDcOnlySaturatingCoefficient_ClampsToByteRange(short dc, byte expected)
    {
        Span<short> coefficients = stackalloc short[64];
        coefficients[0] = dc;
        Span<ushort> quant = stackalloc ushort[64];
        quant.Fill(1);

        var kernel = new AanScalarInverseDct();
        Span<byte> output = stackalloc byte[64];
        kernel.Transform(coefficients, kernel.PrepareDequantTable(quant), output, outputStride: 8);

        foreach (byte b in output)
        {
            Assert.Equal(expected, b);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AanScalarForwardDct_MatchesScalarReference_ForRandomBlocks(int seed)
    {
        // AanScalarForwardDct's internal math is still double (see AanScalarForwardDct.cs) so it should
        // agree with the direct-definition reference far more tightly than the float32 SIMD tier below.
        AssertAanForwardDctKernelAgrees(new AanScalarForwardDct(), seed, tolerance: 1e-6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Vector256AanInverseDct_MatchesScalarReference_ForRandomBlocks(int seed)
    {
        if (!Avx.IsSupported)
        {
            Assert.Skip("No AVX hardware acceleration available on this machine.");
        }

        AssertInverseDctKernelsAgree(new ScalarInverseDct(), new Vector256AanInverseDct(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Vector256AanInverseDct_MatchesScalarAan_ForRandomBlocks(int seed)
    {
        if (!Avx.IsSupported)
        {
            Assert.Skip("No AVX hardware acceleration available on this machine.");
        }

        // Parity against AanScalarInverseDct specifically (not just the direct-definition reference)
        // isolates vectorization bugs (e.g. a wrong Transpose8x8 shuffle index) from algorithm bugs: a
        // scrambled-but-plausible block can still round-trip close enough to the reference to pass the test
        // above, but it won't match the scalar AAN kernel's exact per-pixel output.
        AssertInverseDctKernelsAgree(new AanScalarInverseDct(), new Vector256AanInverseDct(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Vector256AanForwardDct_MatchesScalarReference_ForRandomBlocks(int seed)
    {
        if (!Avx.IsSupported)
        {
            Assert.Skip("No AVX hardware acceleration available on this machine.");
        }

        AssertAanForwardDctKernelAgrees(new Vector256AanForwardDct(), seed, tolerance: 1.0);
    }

    [Fact]
    public void Vector256AanInverseDct_TransformIntoStridedPlane_DoesNotWriteOutsideTheBlock()
    {
        if (!Avx.IsSupported)
        {
            Assert.Skip("No AVX hardware acceleration available on this machine.");
        }

        AssertTransformIntoStridedPlaneDoesNotOverwrite(new Vector256AanInverseDct());
    }

    [Fact]
    public void AanScalarInverseDct_TransformIntoStridedPlane_DoesNotWriteOutsideTheBlock()
    {
        AssertTransformIntoStridedPlaneDoesNotOverwrite(new AanScalarInverseDct());
    }

    [Fact]
    public void Vector256AanForwardDct_TransformOfLastBlockInExactlySizedPlane_DoesNotReadOutOfBounds()
    {
        if (!Avx.IsSupported)
        {
            Assert.Skip("No AVX hardware acceleration available on this machine.");
        }

        AssertTransformOfLastBlockInExactlySizedPlaneDoesNotReadOutOfBounds(new Vector256AanForwardDct());
    }

    private static void AssertTransformIntoStridedPlaneDoesNotOverwrite(IInverseDctKernel kernel)
    {
        const int planeWidth = 40;
        const int planeHeight = 40;
        const int blockX = 13;
        const int blockY = 9;

        var random = new Random(7);
        Span<short> coefficients = stackalloc short[64];
        for (int i = 0; i < 64; i++)
        {
            coefficients[i] = (short)random.Next(-64, 65);
        }

        Span<ushort> quant = stackalloc ushort[64];
        quant.Fill(4);

        var plane = new byte[planeWidth * planeHeight];
        Array.Fill(plane, (byte)0xAA);

        int offset = (blockY * planeWidth) + blockX;
        kernel.Transform(coefficients, kernel.PrepareDequantTable(quant), plane.AsSpan(offset), planeWidth);

        for (int y = 0; y < planeHeight; y++)
        {
            for (int x = 0; x < planeWidth; x++)
            {
                bool insideBlock = x >= blockX && x < blockX + 8 && y >= blockY && y < blockY + 8;
                if (!insideBlock)
                {
                    Assert.True(plane[(y * planeWidth) + x] == 0xAA, $"({x},{y}) outside the transformed block was overwritten");
                }
            }
        }
    }

    private static void AssertTransformOfLastBlockInExactlySizedPlaneDoesNotReadOutOfBounds(IForwardDctKernel kernel)
    {
        const int stride = 8;
        var random = new Random(11);
        var plane = new byte[stride * 8]; // exactly one block, no slack past the last byte
        random.NextBytes(plane);

        Span<double> output = stackalloc double[64];

        // Not expected to throw an IndexOutOfRangeException / AccessViolationException from an over-wide load.
        kernel.Transform(plane, stride, output);
    }

    private static void AssertAanForwardDctKernelAgrees(IForwardDctKernel aan, int seed, double tolerance)
    {
        // Both AAN forward tiers' raw Transform() output carries a per-frequency S(u)*S(v) scale (by design
        // — see AanScaleFactors), so it isn't directly comparable to ScalarForwardDct's output. Dividing by
        // the kernel's own PrepareQuantTable(allOnes) result — exactly how FrameEncoder actually consumes
        // an IForwardDctKernel — removes that scale before comparing.
        var random = new Random(seed);
        Span<byte> input = stackalloc byte[64];
        for (int i = 0; i < 64; i++)
        {
            input[i] = (byte)random.Next(0, 256);
        }

        Span<ushort> quant = stackalloc ushort[64];
        quant.Fill(1);

        var scalar = new ScalarForwardDct();
        var aanScale = aan.PrepareQuantTable(quant);

        Span<double> scalarOutput = stackalloc double[64];
        Span<double> aanRawOutput = stackalloc double[64];
        scalar.Transform(input, inputStride: 8, scalarOutput);
        aan.Transform(input, inputStride: 8, aanRawOutput);

        for (int i = 0; i < 64; i++)
        {
            double aanOutput = aanRawOutput[i] / aanScale[i];
            Assert.True(Math.Abs(scalarOutput[i] - aanOutput) <= tolerance, $"Coefficient {i}: scalar={scalarOutput[i]:F6}, aan={aanOutput:F6}");
        }
    }

    private static void AssertInverseDctKernelsAgree(IInverseDctKernel scalar, IInverseDctKernel simd, int seed)
    {
        var random = new Random(seed);

        // Coefficients drawn the way an encoder actually produces them (forward-DCT + quantize a random 8x8
        // block), not sampled uniformly at random: uniform coefficients in [-256,256] dequantize to samples
        // in the thousands, far outside AanScalarInverseDct's SampleRangeLimit window, so most comparisons
        // degenerated into "both kernels saturated" rather than exercising the transform itself.
        Span<byte> block = stackalloc byte[64];
        for (int i = 0; i < 64; i++)
        {
            block[i] = (byte)random.Next(0, 256);
        }

        Span<ushort> quant = stackalloc ushort[64];
        for (int i = 0; i < 64; i++)
        {
            quant[i] = (ushort)random.Next(1, 33);
        }

        Span<double> forward = stackalloc double[64];
        new ScalarForwardDct().Transform(block, inputStride: 8, forward);

        Span<short> coefficients = stackalloc short[64];
        for (int i = 0; i < 64; i++)
        {
            coefficients[i] = (short)Math.Round(forward[i] / quant[i], MidpointRounding.AwayFromZero);
        }

        Span<byte> scalarOutput = stackalloc byte[64];
        Span<byte> simdOutput = stackalloc byte[64];
        scalar.Transform(coefficients, scalar.PrepareDequantTable(quant), scalarOutput, outputStride: 8);
        simd.Transform(coefficients, simd.PrepareDequantTable(quant), simdOutput, outputStride: 8);

        for (int i = 0; i < 64; i++)
        {
            Assert.True(Math.Abs(scalarOutput[i] - simdOutput[i]) <= 1, $"Sample {i}: scalar={scalarOutput[i]}, simd={simdOutput[i]}");
        }
    }

    private static void AssertForwardDctKernelsAgree(IForwardDctKernel scalar, IForwardDctKernel simd, int seed, double tolerance = 1.0)
    {
        var random = new Random(seed);
        Span<byte> input = stackalloc byte[64];
        for (int i = 0; i < 64; i++)
        {
            input[i] = (byte)random.Next(0, 256);
        }

        Span<double> scalarOutput = stackalloc double[64];
        Span<double> simdOutput = stackalloc double[64];
        scalar.Transform(input, inputStride: 8, scalarOutput);
        simd.Transform(input, inputStride: 8, simdOutput);

        for (int i = 0; i < 64; i++)
        {
            Assert.True(Math.Abs(scalarOutput[i] - simdOutput[i]) <= tolerance, $"Coefficient {i}: scalar={scalarOutput[i]:F2}, simd={simdOutput[i]:F2}");
        }
    }
}
