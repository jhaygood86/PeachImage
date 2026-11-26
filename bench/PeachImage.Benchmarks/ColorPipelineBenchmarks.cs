using BenchmarkDotNet.Attributes;
using PeachImage.Internal.Color;
using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Benchmarks;

/// <summary>
/// Throughput of the color pipeline's kernels: the vectorized transfer functions and 3x3 matrix against their scalar twins, premultiplication,
/// half-float conversion, and whole-image color space conversion. Run pinned to one core (see the repo's benchmarking notes) and with
/// <c>DOTNET_EnableAVX2=0</c> as well to see the 128-bit (NEON-width) tier.
/// </summary>
[MemoryDiagnoser]
public class ColorPipelineBenchmarks
{
    private const int Samples = 1 << 16;

    private float[] _plane = null!;
    private float[] _work = null!;
    private float[] _r = null!;
    private float[] _g = null!;
    private float[] _b = null!;
    private float[] _a = null!;
    private float[] _rgba = null!;
    private Half[] _halves = null!;
    private Image _image = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(1);
        _plane = Enumerable.Range(0, Samples).Select(_ => (float)rng.NextDouble()).ToArray();
        _work = new float[Samples];
        _r = (float[])_plane.Clone();
        _g = (float[])_plane.Clone();
        _b = (float[])_plane.Clone();
        _a = (float[])_plane.Clone();
        _rgba = Enumerable.Range(0, Samples * 4).Select(_ => (float)rng.NextDouble()).ToArray();
        _halves = new Half[Samples];
        RgbaRowKernels.SingleToHalf(_plane, _halves);

        _image = Image.Create(1024, 1024, PixelFormat.Rgba32);
        rng.NextBytes(_image.GetPixelSpan());
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public void SrgbDecodeVector()
    {
        _plane.CopyTo(_work, 0);
        ColorKernels.ToLinear(TransferFunction.Srgb, 0, _work);
    }

    [Benchmark]
    public void SrgbDecodeScalar()
    {
        _plane.CopyTo(_work, 0);
        ColorKernels.ApplyScalar(_work, ColorKernels.Kind.SrgbDecode, 0f);
    }

    [Benchmark]
    public void PqDecodeVector()
    {
        _plane.CopyTo(_work, 0);
        ColorKernels.ToLinear(TransferFunction.Pq, 0, _work);
    }

    [Benchmark]
    public void Matrix3x3Vector() => ColorKernels.Matrix3x3(_r, _g, _b, [0.8f, 0.1f, 0.1f, 0.05f, 0.9f, 0.05f, 0.02f, 0.08f, 0.9f]);

    [Benchmark]
    public void Matrix3x3Scalar() => ColorKernels.Matrix3x3Scalar(_r, _g, _b, [0.8f, 0.1f, 0.1f, 0.05f, 0.9f, 0.05f, 0.02f, 0.08f, 0.9f]);

    [Benchmark]
    public void PremultiplyPlanar() => ColorKernels.Premultiply(_r, _g, _b, _a);

    [Benchmark]
    public void PremultiplyInterleaved() => RgbaRowKernels.Premultiply(_rgba, Samples);

    [Benchmark]
    public void HalfToSingleVector() => RgbaRowKernels.HalfToSingle(_halves, _work);

    [Benchmark]
    public void HalfToSingleScalar() => RgbaRowKernels.HalfToSingleScalar(_halves, _work);

    [Benchmark]
    public void SingleToHalfVector() => RgbaRowKernels.SingleToHalf(_plane, _halves);

    [Benchmark]
    public void SingleToHalfScalar() => RgbaRowKernels.SingleToHalfScalar(_plane, _halves);

    [Benchmark]
    public void ConvertColorSpace_Rgba32_SrgbToDisplayP3()
    {
        using var converted = _image.ConvertColorSpace(NamedColorSpace.DisplayP3);
    }

    [Benchmark]
    public void ConvertTo_Rgba32_ToPremultipliedRgbaF16()
    {
        using var converted = _image.ConvertTo(PixelFormat.PremultipliedRgbaF16);
    }
}
