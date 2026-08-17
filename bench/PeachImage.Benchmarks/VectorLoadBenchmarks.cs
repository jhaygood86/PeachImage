using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace PeachImage.Benchmarks;

/// <summary>
/// Compares <c>Vector128/256.Create(ReadOnlySpan&lt;T&gt;)</c> (bounds-checked span load) against
/// <c>Vector128/256.LoadUnsafe(ref T)</c> (unchecked load) for the exact access pattern the WebP VP8L
/// transform kernels and JPEG DCT kernels use: a fixed-width vector loaded from a known-in-bounds offset
/// inside a tight loop. Exists to settle whether switching those call sites to <c>LoadUnsafe</c> — required
/// for .NET 8 compatibility, since <c>Create(Span&lt;T&gt;)</c> over a *mutable* span only resolves on
/// .NET 9+ — is a regression, a wash, or a genuine win, so the choice is made from a measurement rather
/// than an assumption. Covers the three element types those call sites actually use (uint, byte, float) at
/// both vector widths.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Config(typeof(Config))]
public class VectorLoadBenchmarks
{
    // Runs benchmarks in-process instead of generating/building a throwaway project per job: the repo has
    // several stale `.claude/worktrees/*/bench/PeachImage.Benchmarks/PeachImage.Benchmarks.csproj` copies
    // lying around from past agent sessions, and BenchmarkDotNet's default CsProj toolchain can't
    // disambiguate between them by project name alone.
    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(5)
                .WithIterationCount(20));
        }
    }

    private const int Elements = 8192;
    private const int Reps = 1000;

    private uint[] _uints = null!;
    private byte[] _bytes = null!;
    private float[] _floats = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1);

        _uints = new uint[Elements];
        _bytes = new byte[Elements];
        _floats = new float[Elements];

        for (int i = 0; i < Elements; i++)
        {
            _uints[i] = (uint)random.Next();
            _bytes[i] = (byte)random.Next(256);
            _floats[i] = (float)random.NextDouble();
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("UInt32-Vector128")]
    public uint Create_UInt32_Vector128()
    {
        var acc = Vector128<uint>.Zero;
        ReadOnlySpan<uint> span = _uints;
        int n = Vector128<uint>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc ^= Vector128.Create(span.Slice(i, n));
            }
        }

        return acc.ToScalar();
    }

    [Benchmark]
    [BenchmarkCategory("UInt32-Vector128")]
    public uint LoadUnsafe_UInt32_Vector128()
    {
        var acc = Vector128<uint>.Zero;
        Span<uint> span = _uints;
        int n = Vector128<uint>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc ^= Vector128.LoadUnsafe(ref span[i]);
            }
        }

        return acc.ToScalar();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("UInt32-Vector256")]
    public uint Create_UInt32_Vector256()
    {
        var acc = Vector256<uint>.Zero;
        ReadOnlySpan<uint> span = _uints;
        int n = Vector256<uint>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc ^= Vector256.Create(span.Slice(i, n));
            }
        }

        return acc.ToScalar();
    }

    [Benchmark]
    [BenchmarkCategory("UInt32-Vector256")]
    public uint LoadUnsafe_UInt32_Vector256()
    {
        var acc = Vector256<uint>.Zero;
        Span<uint> span = _uints;
        int n = Vector256<uint>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc ^= Vector256.LoadUnsafe(ref span[i]);
            }
        }

        return acc.ToScalar();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Byte-Vector128")]
    public byte Create_Byte_Vector128()
    {
        var acc = Vector128<byte>.Zero;
        ReadOnlySpan<byte> span = _bytes;
        int n = Vector128<byte>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc ^= Vector128.Create(span.Slice(i, n));
            }
        }

        return acc.ToScalar();
    }

    [Benchmark]
    [BenchmarkCategory("Byte-Vector128")]
    public byte LoadUnsafe_Byte_Vector128()
    {
        var acc = Vector128<byte>.Zero;
        Span<byte> span = _bytes;
        int n = Vector128<byte>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc ^= Vector128.LoadUnsafe(ref span[i]);
            }
        }

        return acc.ToScalar();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Byte-Vector256")]
    public byte Create_Byte_Vector256()
    {
        var acc = Vector256<byte>.Zero;
        ReadOnlySpan<byte> span = _bytes;
        int n = Vector256<byte>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc ^= Vector256.Create(span.Slice(i, n));
            }
        }

        return acc.ToScalar();
    }

    [Benchmark]
    [BenchmarkCategory("Byte-Vector256")]
    public byte LoadUnsafe_Byte_Vector256()
    {
        var acc = Vector256<byte>.Zero;
        Span<byte> span = _bytes;
        int n = Vector256<byte>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc ^= Vector256.LoadUnsafe(ref span[i]);
            }
        }

        return acc.ToScalar();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Float-Vector128")]
    public float Create_Float_Vector128()
    {
        var acc = Vector128<float>.Zero;
        ReadOnlySpan<float> span = _floats;
        int n = Vector128<float>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc += Vector128.Create(span.Slice(i, n));
            }
        }

        return acc.ToScalar();
    }

    [Benchmark]
    [BenchmarkCategory("Float-Vector128")]
    public float LoadUnsafe_Float_Vector128()
    {
        var acc = Vector128<float>.Zero;
        Span<float> span = _floats;
        int n = Vector128<float>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc += Vector128.LoadUnsafe(ref span[i]);
            }
        }

        return acc.ToScalar();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Float-Vector256")]
    public float Create_Float_Vector256()
    {
        var acc = Vector256<float>.Zero;
        ReadOnlySpan<float> span = _floats;
        int n = Vector256<float>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc += Vector256.Create(span.Slice(i, n));
            }
        }

        return acc.ToScalar();
    }

    [Benchmark]
    [BenchmarkCategory("Float-Vector256")]
    public float LoadUnsafe_Float_Vector256()
    {
        var acc = Vector256<float>.Zero;
        Span<float> span = _floats;
        int n = Vector256<float>.Count;

        for (int rep = 0; rep < Reps; rep++)
        {
            for (int i = 0; i + n <= span.Length; i += n)
            {
                acc += Vector256.LoadUnsafe(ref span[i]);
            }
        }

        return acc.ToScalar();
    }
}
