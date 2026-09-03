using System.Runtime.Intrinsics;
using PeachImage.Formats.Avif.Encoder.Av1.ColorConversion;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Vector128Av1RgbToYuvKernel"/>/<see cref="Vector256Av1RgbToYuvKernel"/> agree with
/// <see cref="ScalarAv1RgbToYuvKernel"/> -- the per-kernel-tier counterpart to
/// <see cref="Av1RgbToYuvConverterTests"/>, which only verifies whichever tier the current hardware
/// selected.
/// </summary>
public class Av1RgbToYuvKernelTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Vector128RgbToYuvFullRes_MatchesScalarReference(int seed)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        AssertRgbToYuvAgree(new ScalarAv1RgbToYuvKernel(), new Vector128Av1RgbToYuvKernel(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Vector256RgbToYuvFullRes_MatchesScalarReference(int seed)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("No 256-bit SIMD hardware acceleration (AVX/AVX2) available on this machine.");
        }

        AssertRgbToYuvAgree(new ScalarAv1RgbToYuvKernel(), new Vector256Av1RgbToYuvKernel(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Vector128ConvertMonoChrome_MatchesScalarReference(int seed)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        AssertMonoChromeAgree(new ScalarAv1RgbToYuvKernel(), new Vector128Av1RgbToYuvKernel(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Vector256ConvertMonoChrome_MatchesScalarReference(int seed)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("No 256-bit SIMD hardware acceleration (AVX/AVX2) available on this machine.");
        }

        AssertMonoChromeAgree(new ScalarAv1RgbToYuvKernel(), new Vector256Av1RgbToYuvKernel(), seed);
    }

    private static void AssertRgbToYuvAgree(IAv1RgbToYuvKernel scalar, IAv1RgbToYuvKernel simd, int seed)
    {
        // Deliberately odd, non-vector-width-aligned pixel count to exercise the scalar tail loop too.
        const int pixelCount = 37;
        var random = new Random(seed);
        var rgb = new byte[pixelCount * 3];
        random.NextBytes(rgb);

        var scalarY = new int[pixelCount];
        var scalarCb = new float[pixelCount];
        var scalarCr = new float[pixelCount];
        var simdY = new int[pixelCount];
        var simdCb = new float[pixelCount];
        var simdCr = new float[pixelCount];

        scalar.RgbToYuvFullRes(rgb, scalarY, scalarCb, scalarCr, pixelCount);
        simd.RgbToYuvFullRes(rgb, simdY, simdCb, simdCr, pixelCount);

        for (int i = 0; i < pixelCount; i++)
        {
            // Y is rounded/clamped identically (same bias-and-truncate trick) on every tier, so it should
            // agree exactly; Cb/Cr stay raw unclamped floats, where vector FMA-vs-scalar-mul-add ordering
            // can differ in the last bit or two -- a small numeric tolerance, not a byte tolerance.
            Assert.Equal(scalarY[i], simdY[i]);
            Assert.True(Math.Abs(scalarCb[i] - simdCb[i]) <= 0.01f, $"Cb[{i}]: scalar={scalarCb[i]}, simd={simdCb[i]}");
            Assert.True(Math.Abs(scalarCr[i] - simdCr[i]) <= 0.01f, $"Cr[{i}]: scalar={scalarCr[i]}, simd={simdCr[i]}");
        }
    }

    private static void AssertMonoChromeAgree(IAv1RgbToYuvKernel scalar, IAv1RgbToYuvKernel simd, int seed)
    {
        const int pixelCount = 37;
        var random = new Random(seed);
        var gray = new byte[pixelCount];
        random.NextBytes(gray);

        var scalarY = new int[pixelCount];
        var simdY = new int[pixelCount];
        scalar.ConvertMonoChrome(gray, scalarY, pixelCount);
        simd.ConvertMonoChrome(gray, simdY, pixelCount);

        Assert.Equal(scalarY, simdY);
    }
}
