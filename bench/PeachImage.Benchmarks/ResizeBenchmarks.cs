using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Png;
using PeachImage.Formats.Shared.Resampling;
using SkiaSharp;

namespace PeachImage.Benchmarks;

/// <summary>
/// Resize throughput per <see cref="ResamplingFilter"/>: PeachImage vs. SkiaSharp's <c>SKBitmap.Resize</c>
/// wherever SkiaSharp's public API exposes the identical algorithm — the cubic filters via
/// <c>SKSamplingOptions(new SKCubicResampler(B, C))</c> with the exact same constants PeachImage's
/// <c>CubicBcKernel</c> uses (see <c>ResizeSkiaSharpQualityTests</c> for the correctness side of this same
/// comparison), plus Nearest and Linear. Skia's public resize API has no windowed-sinc (Lanczos2/3/5/8,
/// Welch) or box option, so those filters are PeachImage-only rows — the same "no baseline" pattern
/// <see cref="BmpEncodeBenchmarks"/>/<see cref="GifEncodeBenchmarks"/> already use for gaps in SkiaSharp's
/// own API surface. Both a downscale (thumbnail generation, the common case) and an upscale scenario are
/// measured, to/from the same 1920x1080 photographic source <see cref="PngDecodeBenchmarks"/> already uses.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ResizeBenchmarks
{
    private Image _source = null!;
    private SKBitmap _skiaSource = null!;

    // Isolated Vector128-vs-Vector256 convolver tier comparison, bypassing ResamplingConvolverSelector and
    // Image.Resize entirely (same reasoning as UpsamplingBenchmarks.cs comparing IChromaUpsampler tiers
    // directly). Both passes of a real downscale are covered: per Vector256ResamplingConvolver's remarks,
    // ConvolveHorizontal is expected to show no real difference between tiers (Vector256's implementation
    // delegates straight to Vector128 — there's no natural 8-wide grouping for a single 4-float pixel's
    // taps), while ConvolveVertical is where the genuine 8-lane acceleration should show up.
    [SuppressMessage("Performance", "CA1859", Justification = "Deliberately held as the interface type: these fields exist to compare every IResamplingConvolver tier side by side.")]
    private static readonly IResamplingConvolver Vector128Convolver = new Vector128ResamplingConvolver();

    [SuppressMessage("Performance", "CA1859", Justification = "Deliberately held as the interface type: these fields exist to compare every IResamplingConvolver tier side by side.")]
    private static readonly IResamplingConvolver Vector256Convolver = new Vector256ResamplingConvolver();

    private float[] _convolverSource = null!;
    private float[] _convolverDestination = null!;
    private ResamplingWeightMap _convolverWeights = null!;

    private float[] _verticalConvolverSource = null!;
    private float[] _verticalConvolverDestination = null!;
    private ResamplingWeightMap _verticalConvolverWeights = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        using var stream = File.OpenRead(Path.Combine(assetsDir, "photo_1920x1080_24bpp.png"));
        _source = PngDecoder.Decode(stream);

        _skiaSource = new SKBitmap(new SKImageInfo(_source.Width, _source.Height, SKColorType.Rgb888x, SKAlphaType.Opaque));
        byte[] rgbaBytes = ToRgb888x(_source);
        Marshal.Copy(rgbaBytes, 0, _skiaSource.GetPixels(), rgbaBytes.Length);

        _convolverWeights = new ResamplingWeightMap(_source.Width, 480, ResamplingKernelFactory.Create(ResamplingFilter.Bicubic));
        _convolverSource = new float[_source.Width * _source.Height * ImageResizer.FloatsPerPixel];
        var random = new Random(1);
        for (int i = 0; i < _convolverSource.Length; i++)
        {
            _convolverSource[i] = (float)(random.NextDouble() * 255.0);
        }

        _convolverDestination = new float[480 * _source.Height * ImageResizer.FloatsPerPixel];

        _verticalConvolverWeights = new ResamplingWeightMap(_source.Height, 270, ResamplingKernelFactory.Create(ResamplingFilter.Bicubic));
        _verticalConvolverSource = new float[480 * _source.Height * ImageResizer.FloatsPerPixel];
        for (int i = 0; i < _verticalConvolverSource.Length; i++)
        {
            _verticalConvolverSource[i] = (float)(random.NextDouble() * 255.0);
        }

        _verticalConvolverDestination = new float[480 * 270 * ImageResizer.FloatsPerPixel];
    }

    private static byte[] ToRgb888x(Image source)
    {
        var span = source.GetPixelSpan();
        var rgba = new byte[source.Width * source.Height * 4];
        for (int i = 0, p = 0; i < span.Length; i += 3, p += 4)
        {
            rgba[p] = span[i];
            rgba[p + 1] = span[i + 1];
            rgba[p + 2] = span[i + 2];
            rgba[p + 3] = 255;
        }

        return rgba;
    }

    private Image Downscale(ResamplingFilter filter) => _source.Resize(480, 270, new ResizeOptions { Filter = filter });

    private Image Upscale(ResamplingFilter filter) => _source.Resize(3840, 2160, new ResizeOptions { Filter = filter });

    private SKBitmap DownscaleSkia(SKSamplingOptions options) =>
        _skiaSource.Resize(new SKImageInfo(480, 270, SKColorType.Rgb888x, SKAlphaType.Opaque), options)!;

    private SKBitmap UpscaleSkia(SKSamplingOptions options) =>
        _skiaSource.Resize(new SKImageInfo(3840, 2160, SKColorType.Rgb888x, SKAlphaType.Opaque), options)!;

    // --- NearestNeighbor (SkiaSharp: SKFilterMode.Nearest) ---

    [Benchmark]
    [BenchmarkCategory("NearestNeighbor-Downscale")]
    public Image PeachImage_Downscale_NearestNeighbor() => Downscale(ResamplingFilter.NearestNeighbor);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("NearestNeighbor-Downscale")]
    public SKBitmap SkiaSharp_Downscale_NearestNeighbor() => DownscaleSkia(new SKSamplingOptions(SKFilterMode.Nearest));

    [Benchmark]
    [BenchmarkCategory("NearestNeighbor-Upscale")]
    public Image PeachImage_Upscale_NearestNeighbor() => Upscale(ResamplingFilter.NearestNeighbor);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("NearestNeighbor-Upscale")]
    public SKBitmap SkiaSharp_Upscale_NearestNeighbor() => UpscaleSkia(new SKSamplingOptions(SKFilterMode.Nearest));

    // --- Bilinear (SkiaSharp: SKFilterMode.Linear, no mipmap) ---

    [Benchmark]
    [BenchmarkCategory("Bilinear-Downscale")]
    public Image PeachImage_Downscale_Bilinear() => Downscale(ResamplingFilter.Bilinear);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Bilinear-Downscale")]
    public SKBitmap SkiaSharp_Downscale_Bilinear() => DownscaleSkia(new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

    [Benchmark]
    [BenchmarkCategory("Bilinear-Upscale")]
    public Image PeachImage_Upscale_Bilinear() => Upscale(ResamplingFilter.Bilinear);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Bilinear-Upscale")]
    public SKBitmap SkiaSharp_Upscale_Bilinear() => UpscaleSkia(new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

    // --- Bicubic / CatmullRom (SkiaSharp: SKCubicResampler.CatmullRom, B=0 C=0.5 — numerically identical) ---

    [Benchmark]
    [BenchmarkCategory("Bicubic-Downscale")]
    public Image PeachImage_Downscale_Bicubic() => Downscale(ResamplingFilter.Bicubic);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Bicubic-Downscale")]
    public SKBitmap SkiaSharp_Downscale_Bicubic() => DownscaleSkia(new SKSamplingOptions(SKCubicResampler.CatmullRom));

    [Benchmark]
    [BenchmarkCategory("Bicubic-Upscale")]
    public Image PeachImage_Upscale_Bicubic() => Upscale(ResamplingFilter.Bicubic);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Bicubic-Upscale")]
    public SKBitmap SkiaSharp_Upscale_Bicubic() => UpscaleSkia(new SKSamplingOptions(SKCubicResampler.CatmullRom));

    // --- MitchellNetravali (SkiaSharp: SKCubicResampler.Mitchell, B=1/3 C=1/3) ---

    [Benchmark]
    [BenchmarkCategory("MitchellNetravali-Downscale")]
    public Image PeachImage_Downscale_MitchellNetravali() => Downscale(ResamplingFilter.MitchellNetravali);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("MitchellNetravali-Downscale")]
    public SKBitmap SkiaSharp_Downscale_MitchellNetravali() => DownscaleSkia(new SKSamplingOptions(SKCubicResampler.Mitchell));

    [Benchmark]
    [BenchmarkCategory("MitchellNetravali-Upscale")]
    public Image PeachImage_Upscale_MitchellNetravali() => Upscale(ResamplingFilter.MitchellNetravali);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("MitchellNetravali-Upscale")]
    public SKBitmap SkiaSharp_Upscale_MitchellNetravali() => UpscaleSkia(new SKSamplingOptions(SKCubicResampler.Mitchell));

    // --- Hermite (SkiaSharp: SKCubicResampler(0, 0)) ---

    [Benchmark]
    [BenchmarkCategory("Hermite-Downscale")]
    public Image PeachImage_Downscale_Hermite() => Downscale(ResamplingFilter.Hermite);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Hermite-Downscale")]
    public SKBitmap SkiaSharp_Downscale_Hermite() => DownscaleSkia(new SKSamplingOptions(new SKCubicResampler(0, 0)));

    [Benchmark]
    [BenchmarkCategory("Hermite-Upscale")]
    public Image PeachImage_Upscale_Hermite() => Upscale(ResamplingFilter.Hermite);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Hermite-Upscale")]
    public SKBitmap SkiaSharp_Upscale_Hermite() => UpscaleSkia(new SKSamplingOptions(new SKCubicResampler(0, 0)));

    // --- Spline (SkiaSharp: SKCubicResampler(1, 0)) ---

    [Benchmark]
    [BenchmarkCategory("Spline-Downscale")]
    public Image PeachImage_Downscale_Spline() => Downscale(ResamplingFilter.Spline);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Spline-Downscale")]
    public SKBitmap SkiaSharp_Downscale_Spline() => DownscaleSkia(new SKSamplingOptions(new SKCubicResampler(1, 0)));

    [Benchmark]
    [BenchmarkCategory("Spline-Upscale")]
    public Image PeachImage_Upscale_Spline() => Upscale(ResamplingFilter.Spline);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Spline-Upscale")]
    public SKBitmap SkiaSharp_Upscale_Spline() => UpscaleSkia(new SKSamplingOptions(new SKCubicResampler(1, 0)));

    // --- Robidoux (SkiaSharp: SKCubicResampler(0.37821576, 0.31089257)) ---

    [Benchmark]
    [BenchmarkCategory("Robidoux-Downscale")]
    public Image PeachImage_Downscale_Robidoux() => Downscale(ResamplingFilter.Robidoux);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Robidoux-Downscale")]
    public SKBitmap SkiaSharp_Downscale_Robidoux() => DownscaleSkia(new SKSamplingOptions(new SKCubicResampler(0.37821576f, 0.31089257f)));

    [Benchmark]
    [BenchmarkCategory("Robidoux-Upscale")]
    public Image PeachImage_Upscale_Robidoux() => Upscale(ResamplingFilter.Robidoux);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Robidoux-Upscale")]
    public SKBitmap SkiaSharp_Upscale_Robidoux() => UpscaleSkia(new SKSamplingOptions(new SKCubicResampler(0.37821576f, 0.31089257f)));

    // --- RobidouxSharp (SkiaSharp: SKCubicResampler(0.2620145, 0.3689927)) ---

    [Benchmark]
    [BenchmarkCategory("RobidouxSharp-Downscale")]
    public Image PeachImage_Downscale_RobidouxSharp() => Downscale(ResamplingFilter.RobidouxSharp);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("RobidouxSharp-Downscale")]
    public SKBitmap SkiaSharp_Downscale_RobidouxSharp() => DownscaleSkia(new SKSamplingOptions(new SKCubicResampler(0.2620145f, 0.3689927f)));

    [Benchmark]
    [BenchmarkCategory("RobidouxSharp-Upscale")]
    public Image PeachImage_Upscale_RobidouxSharp() => Upscale(ResamplingFilter.RobidouxSharp);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("RobidouxSharp-Upscale")]
    public SKBitmap SkiaSharp_Upscale_RobidouxSharp() => UpscaleSkia(new SKSamplingOptions(new SKCubicResampler(0.2620145f, 0.3689927f)));

    // --- Box, Lanczos2/3/5/8, Welch: PeachImage only — Skia's public resize API has no equivalent ---

    [Benchmark]
    [BenchmarkCategory("Box-Downscale")]
    public Image PeachImage_Downscale_Box() => Downscale(ResamplingFilter.Box);

    [Benchmark]
    [BenchmarkCategory("Box-Upscale")]
    public Image PeachImage_Upscale_Box() => Upscale(ResamplingFilter.Box);

    [Benchmark]
    [BenchmarkCategory("Welch-Downscale")]
    public Image PeachImage_Downscale_Welch() => Downscale(ResamplingFilter.Welch);

    [Benchmark]
    [BenchmarkCategory("Welch-Upscale")]
    public Image PeachImage_Upscale_Welch() => Upscale(ResamplingFilter.Welch);

    [Benchmark]
    [BenchmarkCategory("Lanczos2-Downscale")]
    public Image PeachImage_Downscale_Lanczos2() => Downscale(ResamplingFilter.Lanczos2);

    [Benchmark]
    [BenchmarkCategory("Lanczos2-Upscale")]
    public Image PeachImage_Upscale_Lanczos2() => Upscale(ResamplingFilter.Lanczos2);

    [Benchmark]
    [BenchmarkCategory("Lanczos3-Downscale")]
    public Image PeachImage_Downscale_Lanczos3() => Downscale(ResamplingFilter.Lanczos3);

    [Benchmark]
    [BenchmarkCategory("Lanczos3-Upscale")]
    public Image PeachImage_Upscale_Lanczos3() => Upscale(ResamplingFilter.Lanczos3);

    [Benchmark]
    [BenchmarkCategory("Lanczos5-Downscale")]
    public Image PeachImage_Downscale_Lanczos5() => Downscale(ResamplingFilter.Lanczos5);

    [Benchmark]
    [BenchmarkCategory("Lanczos5-Upscale")]
    public Image PeachImage_Upscale_Lanczos5() => Upscale(ResamplingFilter.Lanczos5);

    [Benchmark]
    [BenchmarkCategory("Lanczos8-Downscale")]
    public Image PeachImage_Downscale_Lanczos8() => Downscale(ResamplingFilter.Lanczos8);

    [Benchmark]
    [BenchmarkCategory("Lanczos8-Upscale")]
    public Image PeachImage_Upscale_Lanczos8() => Upscale(ResamplingFilter.Lanczos8);

    // --- SIMD tier comparison: Vector128 vs Vector256, bypassing ResamplingConvolverSelector directly ---

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ConvolverTier-Horizontal")]
    public void Vector128Convolver_ConvolveHorizontal() =>
        Vector128Convolver.ConvolveHorizontal(_convolverSource, _source.Width, _source.Height, _convolverDestination, _convolverWeights);

    [Benchmark]
    [BenchmarkCategory("ConvolverTier-Horizontal")]
    public void Vector256Convolver_ConvolveHorizontal() =>
        Vector256Convolver.ConvolveHorizontal(_convolverSource, _source.Width, _source.Height, _convolverDestination, _convolverWeights);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ConvolverTier-Vertical")]
    public void Vector128Convolver_ConvolveVertical() =>
        Vector128Convolver.ConvolveVertical(_verticalConvolverSource, 480, _source.Height, _verticalConvolverDestination, _verticalConvolverWeights);

    [Benchmark]
    [BenchmarkCategory("ConvolverTier-Vertical")]
    public void Vector256Convolver_ConvolveVertical() =>
        Vector256Convolver.ConvolveVertical(_verticalConvolverSource, 480, _source.Height, _verticalConvolverDestination, _verticalConvolverWeights);
}
