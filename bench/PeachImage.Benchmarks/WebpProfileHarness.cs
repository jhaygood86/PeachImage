using System.Diagnostics;
using PeachImage.Formats.Webp;

namespace PeachImage.Benchmarks;

/// <summary>
/// A bare decode-in-a-loop driver for attaching a sampling profiler to one WebP scenario, invoked as
/// <c>PeachImage.Benchmarks.exe profile &lt;scenario&gt; [iterations]</c>. Deliberately *not* a BenchmarkDotNet
/// benchmark: BDN runs each method through a pilot/warmup/overhead/actual sequence inside a generated harness,
/// so an EventPipe trace of it interleaves engine frames with the frames under study for no benefit here. This
/// runs exactly one decode call in a tight loop so the resulting flame graph is entirely decoder frames.
/// </summary>
/// <remarks>
/// SkiaSharp is deliberately never touched on this path — its native <c>libSkiaSharp</c> frames have no managed
/// symbols and would show up as a large unresolvable block in the trace. Comparative timing against SkiaSharp
/// is <see cref="WebpDecodeBenchmarks"/>'s job; this type's only job is attributing PeachImage's own time.
/// </remarks>
internal static class WebpProfileHarness
{
    private const int DefaultIterations = 300;

    /// <summary>Maps a scenario name to the asset <see cref="WebpDecodeBenchmarks"/> uses for the same case, so a profile and a benchmark always describe the same work.</summary>
    private static readonly Dictionary<string, string> Scenarios = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lossy"] = "photo_1920x1080_lossy_q80.webp",
        ["lossless"] = "photo_1920x1080_lossless.webp",
        ["lossy-alpha"] = "photo_1920x1080_alpha_lossy_q80.webp",
        ["lossless-alpha"] = "photo_1920x1080_alpha_lossless.webp",
        ["graphic"] = "graphic_640x480_lossless.webp",
        ["small"] = "small_32x24_lossless.webp",
    };

    /// <summary>Runs <paramref name="args"/>[1] scenario for <paramref name="args"/>[2] iterations. Returns a process exit code.</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 2 || !Scenarios.TryGetValue(args[1], out string? assetName))
        {
            Console.Error.WriteLine($"usage: profile <{string.Join('|', Scenarios.Keys)}> [iterations]");
            return 1;
        }

        int iterations = args.Length >= 3 && int.TryParse(args[2], out int parsed) && parsed > 0
            ? parsed
            : DefaultIterations;

        string assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", assetName);
        byte[] bytes = File.ReadAllBytes(assetPath);

        // One untimed decode so JIT compilation and the first pool rentals land outside the measured region.
        long sink = DecodeOnce(bytes);

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            sink ^= DecodeOnce(bytes);
        }

        stopwatch.Stop();

        Console.WriteLine($"{args[1]}: {iterations} iterations in {stopwatch.Elapsed.TotalMilliseconds:F1} ms " +
                          $"({stopwatch.Elapsed.TotalMilliseconds / iterations:F3} ms/iteration)");

        // Printing the accumulator is what stops the JIT from eliminating the decode as dead code.
        Console.WriteLine($"checksum: {sink:X}");
        return 0;
    }

    /// <summary>Decodes once and folds a few pixels into a value the caller keeps live, so the decode can't be optimized away.</summary>
    private static long DecodeOnce(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = WebpDecoder.Decode(stream);

        var pixels = image.GetPixelSpan();
        long accumulator = pixels.Length;
        accumulator ^= pixels[0];
        accumulator ^= (long)pixels[pixels.Length / 2] << 8;
        accumulator ^= (long)pixels[^1] << 16;
        return accumulator;
    }
}
