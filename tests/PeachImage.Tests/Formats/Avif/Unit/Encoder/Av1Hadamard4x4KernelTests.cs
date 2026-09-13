using System.Runtime.Intrinsics;
using PeachImage.Formats.Avif.Encoder.Av1.IntraModel;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Vector128Av1Hadamard4x4Kernel"/> agrees with <see cref="ScalarAv1Hadamard4x4Kernel"/>
/// -- the per-kernel-tier counterpart to whichever tests exercise <see cref="Av1IntraModelRdPruner.Hadamard4x4"/>
/// only through the current hardware's selected tier. Unlike the floating-point matrix-vector/quantize
/// kernels, this transform is pure integer arithmetic (add/subtract/arithmetic-shift-right-by-1), so the two
/// tiers must agree exactly, not just within a tolerance.
/// </summary>
public class Av1Hadamard4x4KernelTests
{
    [Fact]
    public void Vector128Apply_MatchesScalarReference_AcrossRandomAndEdgeCaseInputs()
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        var scalar = new ScalarAv1Hadamard4x4Kernel();
        var simd = new Vector128Av1Hadamard4x4Kernel();

        var random = new Random(12345);
        for (int trial = 0; trial < 200; trial++)
        {
            int stride = 4 + (trial % 3 == 0 ? 4 : 0);
            var srcDiff = new int[stride * 4];
            for (int i = 0; i < srcDiff.Length; i++)
            {
                srcDiff[i] = random.Next(-2048, 2048);
            }

            AssertAgree(scalar, simd, srcDiff, stride);
        }

        // Edge cases: all zero, all min/max magnitude, alternating sign.
        AssertAgree(scalar, simd, new int[16], 4);
        AssertAgree(scalar, simd, Enumerable.Repeat(2047, 16).ToArray(), 4);
        AssertAgree(scalar, simd, Enumerable.Repeat(-2048, 16).ToArray(), 4);
        AssertAgree(scalar, simd, [.. Enumerable.Range(0, 16).Select(i => i % 2 == 0 ? 2047 : -2048)], 4);
    }

    private static void AssertAgree(IAv1Hadamard4x4Kernel scalar, IAv1Hadamard4x4Kernel simd, int[] srcDiff, int stride)
    {
        var scalarOut = new int[16];
        var simdOut = new int[16];
        scalar.Apply(srcDiff, stride, scalarOut);
        simd.Apply(srcDiff, stride, simdOut);

        Assert.Equal(scalarOut, simdOut);
    }
}
