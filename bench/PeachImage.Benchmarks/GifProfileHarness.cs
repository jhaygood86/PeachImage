using System.Diagnostics;
using PeachImage.Formats.Gif;

namespace PeachImage.Benchmarks;

/// <summary>
/// A bare decode-in-a-loop driver for attaching a sampling profiler to one static GIF decode scenario,
/// invoked as <c>PeachImage.Benchmarks.exe gif-profile &lt;scenario&gt; [iterations]</c>. See
/// <see cref="WebpProfileHarness"/> for why this is deliberately not a BenchmarkDotNet benchmark.
/// </summary>
internal static class GifProfileHarness
{
    private const int DefaultIterations = 300;

    /// <summary>Maps a scenario name to the asset <see cref="GifDecodeBenchmarks"/> uses for the same case, so a profile and a benchmark always describe the same work.</summary>
    private static readonly Dictionary<string, string> Scenarios = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low-color"] = "graphic_640x480_16color.gif",
        ["photo"] = "photo_1920x1080_dithered.gif",
    };

    /// <summary>Runs <paramref name="args"/>[1] scenario for <paramref name="args"/>[2] iterations. Returns a process exit code.</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 2 || !Scenarios.TryGetValue(args[1], out string? assetName))
        {
            Console.Error.WriteLine($"usage: gif-profile <{string.Join('|', Scenarios.Keys)}> [iterations]");
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
        var image = GifDecoder.Decode(stream);

        var pixels = image.GetPixelSpan();
        long accumulator = pixels.Length;
        accumulator ^= pixels[0];
        accumulator ^= (long)pixels[pixels.Length / 2] << 8;
        accumulator ^= (long)pixels[^1] << 16;
        return accumulator;
    }
}
