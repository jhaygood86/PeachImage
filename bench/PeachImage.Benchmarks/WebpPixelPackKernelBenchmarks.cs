using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Webp.Kernels;

namespace PeachImage.Benchmarks;

/// <summary>
/// Isolates <c>WebpFrameEncoder</c>'s per-pixel gather/extract repacking steps from the rest of VP8L/VP8
/// encoding. Entropy coding dominates whole-encode timing (see <see cref="WebpEncodeBenchmarks"/>), so a
/// repacking-only change wouldn't move that benchmark's numbers outside noise even if it were meaningfully
/// faster -- this isolates just the two hardware-tiered kernels themselves.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class WebpPixelPackKernelBenchmarks
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int PixelCount = Width * Height;

    private byte[] _rgbaOpaque = null!;
    private byte[] _rgbaWithAlpha = null!;
    private uint[] _argbSource = null!;
    private uint[] _packedDestination = null!;
    private byte[] _rgbDestination = null!;

    private ScalarWebpPixelPackKernel _scalar = null!;
    private Vector128WebpPixelPackKernel _vector128 = null!;

    [SuppressMessage(
        "Performance",
        "CA1859",
        Justification = "Returns IWebpPixelPackKernel by design: the concrete tier is chosen at runtime from hardware support, so the field cannot be typed to one implementation.")]
    private IWebpPixelPackKernel _bestAvailable = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);

        _rgbaOpaque = new byte[PixelCount * 4];
        random.NextBytes(_rgbaOpaque);
        for (int i = 0; i < PixelCount; i++)
        {
            _rgbaOpaque[(i * 4) + 3] = 0xFF;
        }

        _rgbaWithAlpha = new byte[PixelCount * 4];
        random.NextBytes(_rgbaWithAlpha);

        _argbSource = new uint[PixelCount];
        for (int i = 0; i < PixelCount; i++)
        {
            _argbSource[i] = (uint)random.Next();
        }

        _packedDestination = new uint[PixelCount];
        _rgbDestination = new byte[PixelCount * 3];

        _scalar = new ScalarWebpPixelPackKernel();
        _vector128 = new Vector128WebpPixelPackKernel();
        _bestAvailable = WebpPixelPackKernelSelector.Instance;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("GatherRgba32-Opaque-1920x1080")]
    public bool Scalar_GatherRgba32_Opaque() => _scalar.GatherRgba32(_rgbaOpaque, _packedDestination);

    [Benchmark]
    [BenchmarkCategory("GatherRgba32-Opaque-1920x1080")]
    public bool Vector128_GatherRgba32_Opaque() => _vector128.GatherRgba32(_rgbaOpaque, _packedDestination);

    [Benchmark]
    [BenchmarkCategory("GatherRgba32-Opaque-1920x1080")]
    public bool BestAvailable_GatherRgba32_Opaque() => _bestAvailable.GatherRgba32(_rgbaOpaque, _packedDestination);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("GatherRgba32-WithAlpha-1920x1080")]
    public bool Scalar_GatherRgba32_WithAlpha() => _scalar.GatherRgba32(_rgbaWithAlpha, _packedDestination);

    [Benchmark]
    [BenchmarkCategory("GatherRgba32-WithAlpha-1920x1080")]
    public bool Vector128_GatherRgba32_WithAlpha() => _vector128.GatherRgba32(_rgbaWithAlpha, _packedDestination);

    [Benchmark]
    [BenchmarkCategory("GatherRgba32-WithAlpha-1920x1080")]
    public bool BestAvailable_GatherRgba32_WithAlpha() => _bestAvailable.GatherRgba32(_rgbaWithAlpha, _packedDestination);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ExtractRgb-1920x1080")]
    public void Scalar_ExtractRgb() => _scalar.ExtractRgb(_argbSource, _rgbDestination);

    [Benchmark]
    [BenchmarkCategory("ExtractRgb-1920x1080")]
    public void Vector128_ExtractRgb() => _vector128.ExtractRgb(_argbSource, _rgbDestination);

    [Benchmark]
    [BenchmarkCategory("ExtractRgb-1920x1080")]
    public void BestAvailable_ExtractRgb() => _bestAvailable.ExtractRgb(_argbSource, _rgbDestination);
}
