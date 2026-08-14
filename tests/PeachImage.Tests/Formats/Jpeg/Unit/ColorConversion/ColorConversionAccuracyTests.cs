using System.Runtime.Intrinsics;
using PeachImage.Formats.Jpeg.ColorConversion;

namespace PeachImage.Tests.Formats.Jpeg.Unit.ColorConversion;

public class ColorConversionAccuracyTests
{
    [Fact]
    public void YCbCrToRgb_ThenBack_RoundTripsWithinToleranceForAllYAtZeroChroma()
    {
        var converter = new ScalarColorConverter();
        Span<byte> y = stackalloc byte[1];
        Span<byte> cb = [128];
        Span<byte> cr = [128];
        Span<byte> rgb = stackalloc byte[3];
        for (int yValue = 0; yValue <= 255; yValue++)
        {
            y[0] = (byte)yValue;
            converter.YCbCrToRgb(y, cb, cr, rgb, 1);

            // Zero chroma should map to a neutral gray at (approximately) the same luma.
            Assert.True(Math.Abs(rgb[0] - yValue) <= 1);
            Assert.True(Math.Abs(rgb[1] - yValue) <= 1);
            Assert.True(Math.Abs(rgb[2] - yValue) <= 1);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Vector128YCbCrToRgb_MatchesScalarReference(int seed)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        AssertYCbCrToRgbAgree(new ScalarColorConverter(), new Vector128ColorConverter(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Vector256YCbCrToRgb_MatchesScalarReference(int seed)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("No 256-bit SIMD hardware acceleration (AVX/AVX2) available on this machine.");
        }

        AssertYCbCrToRgbAgree(new ScalarColorConverter(), new Vector256ColorConverter(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Vector128RgbToYCbCr_MatchesScalarReference(int seed)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        AssertRgbToYCbCrAgree(new ScalarColorConverter(), new Vector128ColorConverter(), seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Vector256RgbToYCbCr_MatchesScalarReference(int seed)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("No 256-bit SIMD hardware acceleration (AVX/AVX2) available on this machine.");
        }

        AssertRgbToYCbCrAgree(new ScalarColorConverter(), new Vector256ColorConverter(), seed);
    }

    private static void AssertYCbCrToRgbAgree(IColorConverter scalar, IColorConverter simd, int seed)
    {
        // Deliberately odd, non-vector-width-aligned pixel count to exercise the scalar tail loop too.
        const int pixelCount = 37;
        var random = new Random(seed);
        var y = new byte[pixelCount];
        var cb = new byte[pixelCount];
        var cr = new byte[pixelCount];
        random.NextBytes(y);
        random.NextBytes(cb);
        random.NextBytes(cr);

        var scalarOut = new byte[pixelCount * 3];
        var simdOut = new byte[pixelCount * 3];
        scalar.YCbCrToRgb(y, cb, cr, scalarOut, pixelCount);
        simd.YCbCrToRgb(y, cb, cr, simdOut, pixelCount);

        for (int i = 0; i < scalarOut.Length; i++)
        {
            Assert.True(Math.Abs(scalarOut[i] - simdOut[i]) <= 1, $"Byte {i}: scalar={scalarOut[i]}, simd={simdOut[i]}");
        }
    }

    private static void AssertRgbToYCbCrAgree(IColorConverter scalar, IColorConverter simd, int seed)
    {
        const int pixelCount = 37;
        var random = new Random(seed);
        var rgb = new byte[pixelCount * 3];
        random.NextBytes(rgb);

        var scalarY = new byte[pixelCount];
        var scalarCb = new byte[pixelCount];
        var scalarCr = new byte[pixelCount];
        var simdY = new byte[pixelCount];
        var simdCb = new byte[pixelCount];
        var simdCr = new byte[pixelCount];

        scalar.RgbToYCbCr(rgb, scalarY, scalarCb, scalarCr, pixelCount);
        simd.RgbToYCbCr(rgb, simdY, simdCb, simdCr, pixelCount);

        for (int i = 0; i < pixelCount; i++)
        {
            Assert.True(Math.Abs(scalarY[i] - simdY[i]) <= 1, $"Y[{i}]: scalar={scalarY[i]}, simd={simdY[i]}");
            Assert.True(Math.Abs(scalarCb[i] - simdCb[i]) <= 1, $"Cb[{i}]: scalar={scalarCb[i]}, simd={simdCb[i]}");
            Assert.True(Math.Abs(scalarCr[i] - simdCr[i]) <= 1, $"Cr[{i}]: scalar={scalarCr[i]}, simd={simdCr[i]}");
        }
    }
}
