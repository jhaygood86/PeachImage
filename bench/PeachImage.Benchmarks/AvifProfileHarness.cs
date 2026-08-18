using System.Diagnostics;
using PeachImage.Formats.Avif;

namespace PeachImage.Benchmarks;

/// <summary>
/// A bare decode-in-a-loop driver for attaching a sampling profiler to one AVIF scenario, invoked as
/// <c>PeachImage.Benchmarks.exe avif-profile &lt;scenario&gt; [iterations]</c>. Mirrors
/// <see cref="WebpProfileHarness"/>'s reasoning exactly: BenchmarkDotNet's own pilot/warmup/overhead
/// machinery would interleave engine frames with the frames under study, so this runs exactly one decode
/// call in a tight loop instead.
/// </summary>
internal static class AvifProfileHarness
{
    private const int DefaultIterations = 100;

    private static readonly Dictionary<string, string> Scenarios = new(StringComparer.OrdinalIgnoreCase)
    {
        ["photo420"] = "photo_1920x1080_420.avif",
        ["alpha"] = "photo_1920x1080_alpha.avif",
        ["small"] = "small_32x24.avif",
    };

    public static int Run(string[] args)
    {
        if (args.Length < 2 || !Scenarios.TryGetValue(args[1], out string? assetName))
        {
            Console.Error.WriteLine($"usage: avif-profile <{string.Join('|', Scenarios.Keys)}> [iterations]");
            return 1;
        }

        int iterations = args.Length >= 3 && int.TryParse(args[2], out int parsed) && parsed > 0
            ? parsed
            : DefaultIterations;

        string assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", assetName);
        byte[] bytes = File.ReadAllBytes(assetPath);

        long sink = DecodeOnce(bytes);

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            sink ^= DecodeOnce(bytes);
        }

        stopwatch.Stop();

        Console.WriteLine($"{args[1]}: {iterations} iterations in {stopwatch.Elapsed.TotalMilliseconds:F1} ms " +
                          $"({stopwatch.Elapsed.TotalMilliseconds / iterations:F3} ms/iteration)");
        Console.WriteLine($"checksum: {sink:X}");
        return 0;
    }

    private static long DecodeOnce(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = AvifDecoder.Decode(stream);

        var pixels = image.GetPixelSpan();
        long accumulator = pixels.Length;
        accumulator ^= pixels[0];
        accumulator ^= (long)pixels[pixels.Length / 2] << 8;
        accumulator ^= (long)pixels[^1] << 16;
        return accumulator;
    }
}
