using System.Runtime.Intrinsics;
using PeachImage.Formats.Avif.Decoding.Av1.IntraPrediction;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Vector128Av1PaethKernel"/>/<see cref="Vector256Av1PaethKernel"/> agree exactly with
/// <see cref="ScalarAv1PaethKernel"/> -- this predictor is shared decode/encode code (spec §7.11.2.2), so a
/// mismatch here would mean a real bitstream desync risk, not just an encoder-side inefficiency. Pure integer
/// add/subtract/abs/compare, so all tiers must match bit-for-bit, not within a tolerance.
/// </summary>
public class Av1PaethKernelTests
{
    private static readonly int[] BlockSizes = [4, 8, 16, 32, 64];

    [Theory]
    [MemberData(nameof(WidthHeightPairs))]
    public void Vector128Apply_MatchesScalarReference(int w, int h)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        AssertAgree(new ScalarAv1PaethKernel(), new Vector128Av1PaethKernel(), w, h);
    }

    [Theory]
    [MemberData(nameof(WidthHeightPairs))]
    public void Vector256Apply_MatchesScalarReference(int w, int h)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("No 256-bit SIMD hardware acceleration (AVX/AVX2) available on this machine.");
        }

        AssertAgree(new ScalarAv1PaethKernel(), new Vector256Av1PaethKernel(), w, h);
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

    private static void AssertAgree(IAv1PaethKernel scalar, IAv1PaethKernel simd, int w, int h)
    {
        var random = new Random((w * 1000) + h);
        var aboveRow = new int[w];
        var leftCol = new int[h];
        for (int i = 0; i < w; i++)
        {
            aboveRow[i] = random.Next(0, 256);
        }

        for (int i = 0; i < h; i++)
        {
            leftCol[i] = random.Next(0, 256);
        }

        int aboveMinus1 = random.Next(0, 256);

        var scalarOut = new int[w * h];
        var simdOut = new int[w * h];
        scalar.Apply(scalarOut, w, h, aboveRow, leftCol, aboveMinus1);
        simd.Apply(simdOut, w, h, aboveRow, leftCol, aboveMinus1);

        Assert.Equal(scalarOut, simdOut);
    }
}
