using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Webp.Decoding.Vp8.Dct;
using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Benchmarks;

/// <summary>
/// Per-kernel cost of the VP8 encoder's per-4x4-block pipeline (forward DCT, quantize, dequantize, inverse DCT
/// + add), to see which stage matters. Each invocation runs 10,000 blocks over a 256x256 source/prediction pair.
/// </summary>
[MemoryDiagnoser]
public class WebpEncoderKernelBenchmarks
{
    private const int Blocks = 10_000;
    private const int Stride = 256;

    private byte[] _source = null!;
    private byte[] _prediction = null!;
    private short[] _coeffs = null!;
    private short[] _quantized = null!;
    private short[] _dequantized = null!;
    private readonly short[] _raw = new short[16];

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(9);
        _source = new byte[Stride * Stride];
        _prediction = new byte[Stride * Stride];
        rng.NextBytes(_source);
        for (int i = 0; i < _prediction.Length; i++)
        {
            _prediction[i] = (byte)Math.Clamp(_source[i] + rng.Next(-20, 20), 0, 255);
        }

        _coeffs = new short[16];
        _quantized = new short[16];
        _dequantized = new short[16];
        Vp8ForwardDct.Transform(_source, 0, Stride, _prediction, 0, Stride, _coeffs);
        Vp8ForwardQuantizer.Quantize(_coeffs, 20, 24, _quantized);
        Vp8ForwardQuantizer.Dequantize(_quantized, 20, 24, _dequantized);
    }

    private static int Origin(int i) => (((i % 4096) / 64) * 4 * Stride) + ((i % 64) * 4);

    [Benchmark]
    public int ForwardDct()
    {
        for (int i = 0; i < Blocks; i++)
        {
            int o = Origin(i);
            Vp8ForwardDct.Transform(_source, o, Stride, _prediction, o, Stride, _raw);
        }

        return _raw[0];
    }

    [Benchmark]
    public int Quantize()
    {
        int last = 0;
        for (int i = 0; i < Blocks; i++)
        {
            last += Vp8ForwardQuantizer.Quantize(_coeffs, 20, 24, _quantized);
        }

        return last;
    }

    [Benchmark]
    public int Dequantize()
    {
        for (int i = 0; i < Blocks; i++)
        {
            Vp8ForwardQuantizer.Dequantize(_quantized, 20, 24, _dequantized);
        }

        return _dequantized[0];
    }

    [Benchmark]
    public int InverseDctAndAdd()
    {
        var plane = _prediction;
        for (int i = 0; i < Blocks; i++)
        {
            Vp8ScalarInverseDct.TransformAndAdd(_dequantized, plane, Origin(i), Stride);
        }

        return plane[0];
    }
}
