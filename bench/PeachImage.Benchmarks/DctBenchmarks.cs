using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Jpeg.Dct;

namespace PeachImage.Benchmarks;

/// <summary>
/// Isolated per-kernel-tier DCT throughput, bypassing <c>DctKernelSelector</c> so each tier can be measured
/// directly against the others. <c>SingleBlock</c> benchmarks measure one 8x8 <c>Transform</c> call in
/// isolation; <c>ManyBlocks</c> benchmarks loop 10,000 calls per invocation, closer to the call volume a
/// real decode/encode actually drives through these kernels (e.g. a 1080p 4:2:0 frame is ~4,000 blocks).
/// <c>DctKernelSelector</c> currently selects <see cref="Vector256AanInverseDct"/>/<see cref="Vector256AanForwardDct"/>
/// when AVX is available, falling back to <see cref="AanScalarInverseDct"/>/<see cref="AanScalarForwardDct"/> otherwise.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class DctBenchmarks
{
    private const int ManyBlocksIterations = 10_000;

    private static readonly IInverseDctKernel ScalarInverse = new ScalarInverseDct();
    private static readonly IInverseDctKernel Vector128Inverse = new Vector128InverseDct();
    private static readonly IInverseDctKernel Vector256Inverse = new Vector256InverseDct();
    private static readonly IInverseDctKernel FastScalarInverse = new FastScalarInverseDct();
    private static readonly IInverseDctKernel AanInverse = new AanScalarInverseDct();
    private static readonly IInverseDctKernel Vector256AanInverse = new Vector256AanInverseDct();

    [SuppressMessage("Performance", "CA1859", Justification = "Deliberately held as the interface type: these fields exist to compare every IForwardDctKernel tier side by side.")]
    private static readonly IForwardDctKernel ScalarForward = new ScalarForwardDct();

    [SuppressMessage("Performance", "CA1859", Justification = "Deliberately held as the interface type: these fields exist to compare every IForwardDctKernel tier side by side.")]
    private static readonly IForwardDctKernel Vector128Forward = new Vector128ForwardDct();

    [SuppressMessage("Performance", "CA1859", Justification = "Deliberately held as the interface type: these fields exist to compare every IForwardDctKernel tier side by side.")]
    private static readonly IForwardDctKernel Vector256Forward = new Vector256ForwardDct();

    [SuppressMessage("Performance", "CA1859", Justification = "Deliberately held as the interface type: these fields exist to compare every IForwardDctKernel tier side by side.")]
    private static readonly IForwardDctKernel FastScalarForward = new FastScalarForwardDct();

    [SuppressMessage("Performance", "CA1859", Justification = "Deliberately held as the interface type: these fields exist to compare every IForwardDctKernel tier side by side.")]
    private static readonly IForwardDctKernel AanForward = new AanScalarForwardDct();

    [SuppressMessage("Performance", "CA1859", Justification = "Deliberately held as the interface type: these fields exist to compare every IForwardDctKernel tier side by side.")]
    private static readonly IForwardDctKernel Vector256AanForward = new Vector256AanForwardDct();

    private short[] _coefficients = null!;
    private float[] _scalarDequant = null!;
    private float[] _vector128Dequant = null!;
    private float[] _vector256Dequant = null!;
    private float[] _fastScalarDequant = null!;
    private float[] _aanDequant = null!;
    private float[] _vector256AanDequant = null!;
    private byte[] _inverseOutput = null!;

    private byte[] _forwardInput = null!;
    private double[] _forwardOutput = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1);

        _coefficients = new short[64];
        for (int i = 0; i < 64; i++)
        {
            _coefficients[i] = (short)random.Next(-256, 257);
        }

        var quant = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            quant[i] = (ushort)random.Next(1, 33);
        }

        // Prepared once here, exactly as FrameReconstructor prepares it once per component, not per block.
        _scalarDequant = ScalarInverse.PrepareDequantTable(quant);
        _vector128Dequant = Vector128Inverse.PrepareDequantTable(quant);
        _vector256Dequant = Vector256Inverse.PrepareDequantTable(quant);
        _fastScalarDequant = FastScalarInverse.PrepareDequantTable(quant);
        _aanDequant = AanInverse.PrepareDequantTable(quant);
        _vector256AanDequant = Vector256AanInverse.PrepareDequantTable(quant);
        _inverseOutput = new byte[64];

        _forwardInput = new byte[64];
        random.NextBytes(_forwardInput);
        _forwardOutput = new double[64];
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Inverse-SingleBlock")]
    public void ScalarInverse_SingleBlock() => ScalarInverse.Transform(_coefficients, _scalarDequant, _inverseOutput, outputStride: 8);

    [Benchmark]
    [BenchmarkCategory("Inverse-SingleBlock")]
    public void Vector128Inverse_SingleBlock() => Vector128Inverse.Transform(_coefficients, _vector128Dequant, _inverseOutput, outputStride: 8);

    [Benchmark]
    [BenchmarkCategory("Inverse-SingleBlock")]
    public void Vector256Inverse_SingleBlock() => Vector256Inverse.Transform(_coefficients, _vector256Dequant, _inverseOutput, outputStride: 8);

    [Benchmark]
    [BenchmarkCategory("Inverse-SingleBlock")]
    public void FastScalarInverse_SingleBlock() => FastScalarInverse.Transform(_coefficients, _fastScalarDequant, _inverseOutput, outputStride: 8);

    [Benchmark]
    [BenchmarkCategory("Inverse-SingleBlock")]
    public void AanInverse_SingleBlock() => AanInverse.Transform(_coefficients, _aanDequant, _inverseOutput, outputStride: 8);

    [Benchmark]
    [BenchmarkCategory("Inverse-SingleBlock")]
    public void Vector256AanInverse_SingleBlock() => Vector256AanInverse.Transform(_coefficients, _vector256AanDequant, _inverseOutput, outputStride: 8);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Inverse-ManyBlocks")]
    public void ScalarInverse_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            ScalarInverse.Transform(_coefficients, _scalarDequant, _inverseOutput, outputStride: 8);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Inverse-ManyBlocks")]
    public void Vector128Inverse_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            Vector128Inverse.Transform(_coefficients, _vector128Dequant, _inverseOutput, outputStride: 8);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Inverse-ManyBlocks")]
    public void Vector256Inverse_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            Vector256Inverse.Transform(_coefficients, _vector256Dequant, _inverseOutput, outputStride: 8);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Inverse-ManyBlocks")]
    public void FastScalarInverse_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            FastScalarInverse.Transform(_coefficients, _fastScalarDequant, _inverseOutput, outputStride: 8);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Inverse-ManyBlocks")]
    public void AanInverse_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            AanInverse.Transform(_coefficients, _aanDequant, _inverseOutput, outputStride: 8);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Inverse-ManyBlocks")]
    public void Vector256AanInverse_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            Vector256AanInverse.Transform(_coefficients, _vector256AanDequant, _inverseOutput, outputStride: 8);
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Forward-SingleBlock")]
    public void ScalarForward_SingleBlock() => ScalarForward.Transform(_forwardInput, inputStride: 8, _forwardOutput);

    [Benchmark]
    [BenchmarkCategory("Forward-SingleBlock")]
    public void Vector128Forward_SingleBlock() => Vector128Forward.Transform(_forwardInput, inputStride: 8, _forwardOutput);

    [Benchmark]
    [BenchmarkCategory("Forward-SingleBlock")]
    public void Vector256Forward_SingleBlock() => Vector256Forward.Transform(_forwardInput, inputStride: 8, _forwardOutput);

    [Benchmark]
    [BenchmarkCategory("Forward-SingleBlock")]
    public void FastScalarForward_SingleBlock() => FastScalarForward.Transform(_forwardInput, inputStride: 8, _forwardOutput);

    [Benchmark]
    [BenchmarkCategory("Forward-SingleBlock")]
    public void AanForward_SingleBlock() => AanForward.Transform(_forwardInput, inputStride: 8, _forwardOutput);

    [Benchmark]
    [BenchmarkCategory("Forward-SingleBlock")]
    public void Vector256AanForward_SingleBlock() => Vector256AanForward.Transform(_forwardInput, inputStride: 8, _forwardOutput);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Forward-ManyBlocks")]
    public void ScalarForward_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            ScalarForward.Transform(_forwardInput, inputStride: 8, _forwardOutput);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Forward-ManyBlocks")]
    public void Vector128Forward_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            Vector128Forward.Transform(_forwardInput, inputStride: 8, _forwardOutput);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Forward-ManyBlocks")]
    public void Vector256Forward_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            Vector256Forward.Transform(_forwardInput, inputStride: 8, _forwardOutput);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Forward-ManyBlocks")]
    public void FastScalarForward_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            FastScalarForward.Transform(_forwardInput, inputStride: 8, _forwardOutput);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Forward-ManyBlocks")]
    public void AanForward_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            AanForward.Transform(_forwardInput, inputStride: 8, _forwardOutput);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Forward-ManyBlocks")]
    public void Vector256AanForward_ManyBlocks()
    {
        for (int i = 0; i < ManyBlocksIterations; i++)
        {
            Vector256AanForward.Transform(_forwardInput, inputStride: 8, _forwardOutput);
        }
    }
}
