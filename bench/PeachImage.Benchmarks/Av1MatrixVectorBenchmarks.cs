using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Avif.Encoder.Av1.Transform;

namespace PeachImage.Benchmarks;

/// <summary>
/// Per-tier throughput of the AV1 encoder's forward-transform matrix-vector kernel (the dense float64 DCT
/// matrix times a row/column), per transform size. One invocation applies the kernel 1,000 times.
/// </summary>
public class Av1MatrixVectorBenchmarks
{
    private const int Repeats = 1000;

    private static readonly ScalarAv1MatrixVectorKernel Scalar = new();
    private static readonly Vector128Av1MatrixVectorKernel Vector128Tier = new();

    private double[,] _matrix = null!;
    private double[] _input = null!;
    private double[] _output = null!;

    [Params(8, 16, 32)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(Size);
        _matrix = new double[Size, Size];
        _input = new double[Size];
        _output = new double[Size];
        for (int r = 0; r < Size; r++)
        {
            _input[r] = rng.Next(-255, 256);
            for (int c = 0; c < Size; c++)
            {
                _matrix[r, c] = Math.Cos(Math.PI * (c + 0.5) * r / Size) * 0.25;
            }
        }
    }

    [Benchmark(Baseline = true)]
    public double ScalarKernel()
    {
        for (int i = 0; i < Repeats; i++)
        {
            Scalar.Apply(_matrix, _input, _output, Size);
        }

        return _output[0];
    }

    [Benchmark]
    public double Vector128Kernel()
    {
        for (int i = 0; i < Repeats; i++)
        {
            Vector128Tier.Apply(_matrix, _input, _output, Size);
        }

        return _output[0];
    }
}
