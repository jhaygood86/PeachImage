using System.Runtime.Intrinsics;
using PeachImage.Formats.Avif.Encoder.Av1.Variance;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Vector128Av1BlockSumSquaresKernel"/>/<see cref="Vector256Av1BlockSumSquaresKernel"/>
/// agree exactly with <see cref="ScalarAv1BlockSumSquaresKernel"/>. Pure integer sum/sum-of-squares, so all
/// tiers must match bit-for-bit, not within a tolerance.
/// </summary>
public class Av1BlockSumSquaresKernelTests
{
    private static readonly int[] BlockSizes = [1, 3, 4, 8, 15, 16, 32, 64];

    [Theory]
    [MemberData(nameof(WidthHeightPairs))]
    public void Vector128Compute_MatchesScalarReference(int w, int h)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        AssertAgree(new ScalarAv1BlockSumSquaresKernel(), new Vector128Av1BlockSumSquaresKernel(), w, h);
    }

    [Theory]
    [MemberData(nameof(WidthHeightPairs))]
    public void Vector256Compute_MatchesScalarReference(int w, int h)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("No 256-bit SIMD hardware acceleration (AVX/AVX2) available on this machine.");
        }

        AssertAgree(new ScalarAv1BlockSumSquaresKernel(), new Vector256Av1BlockSumSquaresKernel(), w, h);
    }

    public static IEnumerable<object[]> WidthHeightPairs()
    {
        foreach (int w in BlockSizes)
        {
            foreach (int h in BlockSizes)
            {
                yield return [w, h];
            }
        }
    }

    private static void AssertAgree(IAv1BlockSumSquaresKernel scalar, IAv1BlockSumSquaresKernel simd, int w, int h)
    {
        var random = new Random((w * 1000) + h);

        // Larger than w x h, with a nonzero (x, y) offset, so the kernel's own stride/offset arithmetic (not
        // just a tightly-packed w x h buffer) is exercised the same way real callers use it.
        int stride = w + 7;
        int planeH = h + 5;
        var source = new int[stride * planeH];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = random.Next(0, 256);
        }

        int x = 3;
        int y = 2;

        scalar.Compute(source, stride, x, y, w, h, out long scalarSum, out long scalarSumSquares);
        simd.Compute(source, stride, x, y, w, h, out long simdSum, out long simdSumSquares);

        Assert.Equal(scalarSum, simdSum);
        Assert.Equal(scalarSumSquares, simdSumSquares);
    }
}
