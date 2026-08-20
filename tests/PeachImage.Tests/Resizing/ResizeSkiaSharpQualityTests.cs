using System.Runtime.InteropServices;
using SkiaSharp;

namespace PeachImage.Tests.Resizing;

/// <summary>
/// Differential quality check against SkiaSharp for every <see cref="ResamplingFilter"/> that has a
/// same-algorithm SkiaSharp equivalent — <c>SKSamplingOptions(new SKCubicResampler(B, C))</c> accepts
/// arbitrary <c>(B, C)</c>, so PeachImage's <c>CubicBcKernel</c>-backed filters run through SkiaSharp with
/// the exact same constants, making this a genuine same-algorithm comparison rather than an approximate one.
/// This is a correctness check, not just a benchmark: it's what caught a coefficient bug in
/// <c>CubicBcKernel</c>'s <c>1 &lt;= |x| &lt; 2</c> piece during development (every filter with <c>C != 0</c>
/// diverged sharply from SkiaSharp until it was fixed).
/// </summary>
/// <remarks>
/// Test content is a smooth, low-frequency synthetic gradient rather than random per-pixel noise: two
/// independent resizers can legitimately use slightly different sub-pixel-center conventions (confirmed
/// empirically here — SkiaSharp's nearest-neighbor picks the source pixel at <c>floor(destIndex * scale)</c>
/// while PeachImage uses the more common pixel-center convention <c>floor((destIndex + 0.5) * scale)</c>),
/// and on high-frequency/uncorrelated content even a half-pixel offset produces large per-pixel differences
/// that don't reflect an actual quality or correctness problem. Smooth content keeps that same convention
/// difference's effect on the *average* difference small, so the tolerances below stay tight enough to catch
/// a genuine regression.
/// </remarks>
public class ResizeSkiaSharpQualityTests
{
    private const int SourceSize = 256;

    public static IEnumerable<object[]> CubicFamilyCases()
    {
        yield return [ResamplingFilter.Bicubic, SKCubicResampler.CatmullRom];
        yield return [ResamplingFilter.CatmullRom, SKCubicResampler.CatmullRom];
        yield return [ResamplingFilter.MitchellNetravali, SKCubicResampler.Mitchell];
        yield return [ResamplingFilter.Hermite, new SKCubicResampler(0, 0)];
        yield return [ResamplingFilter.Spline, new SKCubicResampler(1, 0)];
        yield return [ResamplingFilter.Robidoux, new SKCubicResampler(0.37821576f, 0.31089257f)];
        yield return [ResamplingFilter.RobidouxSharp, new SKCubicResampler(0.2620145f, 0.3689927f)];
    }

    [Theory]
    [MemberData(nameof(CubicFamilyCases))]
    public void CubicFamilyFilter_MatchesSkiaSharp_SameBcConstants(ResamplingFilter filter, SKCubicResampler resampler)
    {
        AssertMatchesSkiaSharp(filter, new SKSamplingOptions(resampler), destSize: 64, maxAverageDifference: 3.0);
        AssertMatchesSkiaSharp(filter, new SKSamplingOptions(resampler), destSize: 480, maxAverageDifference: 1.0);
    }

    [Fact]
    public void Bilinear_MatchesSkiaSharpLinearFilter()
    {
        var options = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        AssertMatchesSkiaSharp(ResamplingFilter.Bilinear, options, destSize: 64, maxAverageDifference: 3.0);
        AssertMatchesSkiaSharp(ResamplingFilter.Bilinear, options, destSize: 480, maxAverageDifference: 1.0);
    }

    [Fact]
    public void NearestNeighbor_RoughlyMatchesSkiaSharp_DespiteDifferingPixelCenterConvention()
    {
        // Looser tolerance than the weighted filters above: NearestNeighbor has no sub-pixel blending to
        // smooth over the two libraries' differing pixel-center conventions (see the type-level remarks),
        // so a modest systematic offset is expected here even on smooth content — this still catches a
        // genuinely broken mapping (which measured far higher, ~30-85, against random noise content during
        // development) without being sensitive to the convention difference itself.
        var options = new SKSamplingOptions(SKFilterMode.Nearest);
        AssertMatchesSkiaSharp(ResamplingFilter.NearestNeighbor, options, destSize: 64, maxAverageDifference: 15.0);
        AssertMatchesSkiaSharp(ResamplingFilter.NearestNeighbor, options, destSize: 480, maxAverageDifference: 1.0);
    }

    private static void AssertMatchesSkiaSharp(ResamplingFilter filter, SKSamplingOptions options, int destSize, double maxAverageDifference)
    {
        var source = CreateSmoothGradientSource();
        using var skiaSource = ToSkiaBitmap(source);

        var resized = source.Resize(destSize, destSize, new ResizeOptions { Filter = filter });
        using var skiaResized = skiaSource.Resize(new SKImageInfo(destSize, destSize, SKColorType.Rgba8888, SKAlphaType.Unpremul), options);
        Assert.NotNull(skiaResized);

        double averageDifference = ComputeAverageRgbDifference(resized, skiaResized!, destSize);
        Assert.True(
            averageDifference < maxAverageDifference,
            $"{filter} at {destSize}x{destSize}: average per-channel difference from SkiaSharp too high: {averageDifference:F3}");
    }

    private static Image CreateSmoothGradientSource()
    {
        var image = Image.Create(SourceSize, SourceSize, PixelFormat.Rgba32);
        var span = image.GetPixelSpan();

        for (int y = 0; y < SourceSize; y++)
        {
            for (int x = 0; x < SourceSize; x++)
            {
                int o = ((y * SourceSize) + x) * 4;
                span[o] = (byte)(((Math.Sin(x / 20.0) * 0.5) + 0.5) * 255);
                span[o + 1] = (byte)(((Math.Cos(y / 15.0) * 0.5) + 0.5) * 255);
                span[o + 2] = (byte)((x + y) % 256);
                span[o + 3] = 255;
            }
        }

        return image;
    }

    private static SKBitmap ToSkiaBitmap(Image source)
    {
        var bitmap = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        byte[] pixelBytes = source.GetPixelSpan().ToArray();
        Marshal.Copy(pixelBytes, 0, bitmap.GetPixels(), pixelBytes.Length);
        return bitmap;
    }

    private static double ComputeAverageRgbDifference(Image peachImage, SKBitmap skiaBitmap, int size)
    {
        var span = peachImage.GetPixelSpan();
        double sum = 0;
        long count = 0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var skiaPixel = skiaBitmap.GetPixel(x, y);
                int offset = ((y * size) + x) * 4;
                sum += Math.Abs(span[offset] - skiaPixel.Red) + Math.Abs(span[offset + 1] - skiaPixel.Green) + Math.Abs(span[offset + 2] - skiaPixel.Blue);
                count++;
            }
        }

        return sum / (count * 3);
    }
}
