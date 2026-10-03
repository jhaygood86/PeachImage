using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Benchmarks;

/// <summary>
/// Isolated AV1 2D inverse DCT_DCT throughput: the batched <c>Vector256&lt;long&gt;</c> <c>Inverse2D</c> against a
/// plain scalar 2D built from the per-row <c>InverseDct</c>. Exists to check whether the batched kernel
/// still wins on hardware where <c>Vector256</c> is not accelerated (Apple Silicon / NEON).
/// </summary>
[MemoryDiagnoser]
public class Av1InverseTransformBenchmarks
{
    private const int Iterations = 2_000;

    private int[] _dequant = null!;
    private int[] _residual = null!;
    private int[] _scalarResidual = null!;
    private int[] _row = null!;

    [Params(Av1TxSize.Tx8x8, Av1TxSize.Tx16x16, Av1TxSize.Tx32x32)]
    public int TxSz { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dequant = new int[64 * 64];
        _residual = new int[64 * 64];
        _scalarResidual = new int[64 * 64];
        _row = new int[64];
        var rng = new Random(7);
        for (int i = 0; i < 32; i++)
        {
            for (int j = 0; j < 32; j++)
            {
                _dequant[(i * 64) + j] = rng.Next(-2000, 2000) >> ((i + j) / 4);
            }
        }

        Av1InverseTransform.Inverse2D(_dequant, _residual, TxSz, Av1TxType.DctDct, false, 8);
        Scalar2D();
        int n = Av1TxDimensions.Width[TxSz] * Av1TxDimensions.Height[TxSz];
        if (!_residual.AsSpan(0, n).SequenceEqual(_scalarResidual.AsSpan(0, n)))
        {
            throw new InvalidOperationException("Scalar reference does not match Inverse2D.");
        }
    }

    [Benchmark(Baseline = true)]
    public int Batched()
    {
        for (int k = 0; k < Iterations; k++)
        {
            Av1InverseTransform.Inverse2D(_dequant, _residual, TxSz, Av1TxType.DctDct, false, 8);
        }

        return _residual[0];
    }

    [Benchmark]
    public int Scalar()
    {
        for (int k = 0; k < Iterations; k++)
        {
            Scalar2D();
        }

        return _scalarResidual[0];
    }

    private void Scalar2D()
    {
        int log2W = Av1TxDimensions.WidthLog2[TxSz];
        int log2H = Av1TxDimensions.HeightLog2[TxSz];
        int w = 1 << log2W;
        int h = 1 << log2H;
        int[] shifts = [0, 1, 2, 2, 2, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2];
        int rowShift = shifts[TxSz];
        const int bitDepth = 8;
        int rowClamp = bitDepth + 8;
        int colClamp = Math.Max(bitDepth + 6, 16);
        bool rescale = Math.Abs(log2W - log2H) == 1;
        int[] res = _scalarResidual;

        for (int i = 0; i < h; i++)
        {
            for (int j = 0; j < w; j++)
            {
                long v = i < 32 && j < 32 ? _dequant[(i * 64) + j] : 0;
                if (rescale)
                {
                    v = (v * 2896 + 2048) >> 12;
                }

                _row[j] = (int)v;
            }

            Av1InverseTransform.InverseDct(_row, log2W, rowClamp);
            for (int j = 0; j < w; j++)
            {
                res[(i * w) + j] = rowShift == 0 ? _row[j] : (int)(((long)_row[j] + (1L << (rowShift - 1))) >> rowShift);
            }
        }

        int colBound = 1 << (colClamp - 1);
        for (int k = 0; k < h * w; k++)
        {
            res[k] = Math.Clamp(res[k], -colBound, colBound - 1);
        }

        for (int j = 0; j < w; j++)
        {
            for (int i = 0; i < h; i++)
            {
                _row[i] = res[(i * w) + j];
            }

            Av1InverseTransform.InverseDct(_row, log2H, colClamp);
            for (int i = 0; i < h; i++)
            {
                res[(i * w) + j] = (int)(((long)_row[i] + 8) >> 4);
            }
        }
    }
}
