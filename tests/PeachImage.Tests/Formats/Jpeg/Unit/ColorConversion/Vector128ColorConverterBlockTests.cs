using System.Runtime.Intrinsics;
using PeachImage.Formats.Jpeg.ColorConversion;
using Xunit;

namespace PeachImage.Tests.Formats.Jpeg.Unit.ColorConversion;

/// <summary>
/// <see cref="Vector128ColorConverter"/> processes 16 pixels per iteration, then 4, then one at a time. These
/// pixel counts straddle every boundary between those stages, and the buffers have no slack past the last
/// pixel, so an out-of-bounds vector load or store shows up as an exception rather than silent corruption.
/// </summary>
public class Vector128ColorConverterBlockTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(100)]
    public void AllConversions_MatchScalar_AtBlockBoundaries(int pixelCount)
    {
        Assert.SkipUnless(Vector128.IsHardwareAccelerated, "Vector128 is not hardware accelerated here.");

        var scalar = new ScalarColorConverter();
        var simd = new Vector128ColorConverter();
        var rng = new Random(pixelCount + 100);

        byte[] Random(int n)
        {
            var b = new byte[n];
            rng.NextBytes(b);
            return b;
        }

        // Extremes in the first few pixels so clamping and rounding edges are always hit.
        byte[] y = Random(pixelCount), cb = Random(pixelCount), cr = Random(pixelCount), k = Random(pixelCount);
        for (int i = 0; i < Math.Min(pixelCount, 4); i++)
        {
            y[i] = (byte)(i % 2 == 0 ? 0 : 255);
            cb[i] = (byte)(i < 2 ? 0 : 255);
            cr[i] = (byte)(i % 2 == 0 ? 255 : 0);
        }

        var rgbScalar = new byte[pixelCount * 3];
        var rgbSimd = new byte[pixelCount * 3];
        scalar.YCbCrToRgb(y, cb, cr, rgbScalar, pixelCount);
        simd.YCbCrToRgb(y, cb, cr, rgbSimd, pixelCount);
        Assert.Equal(rgbScalar, rgbSimd);

        var cmykScalar = new byte[pixelCount * 4];
        var cmykSimd = new byte[pixelCount * 4];
        scalar.YcckToCmyk(y, cb, cr, k, cmykScalar, pixelCount);
        simd.YcckToCmyk(y, cb, cr, k, cmykSimd, pixelCount);
        Assert.Equal(cmykScalar, cmykSimd);

        byte[] rgb = Random(pixelCount * 3);
        byte[] y1 = new byte[pixelCount], cb1 = new byte[pixelCount], cr1 = new byte[pixelCount];
        byte[] y2 = new byte[pixelCount], cb2 = new byte[pixelCount], cr2 = new byte[pixelCount];
        scalar.RgbToYCbCr(rgb, y1, cb1, cr1, pixelCount);
        simd.RgbToYCbCr(rgb, y2, cb2, cr2, pixelCount);
        Assert.Equal(y1, y2);
        Assert.Equal(cb1, cb2);
        Assert.Equal(cr1, cr2);
    }
}
