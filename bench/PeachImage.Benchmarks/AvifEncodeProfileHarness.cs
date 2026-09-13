using System.Diagnostics;
using PeachImage.Formats.Avif;

namespace PeachImage.Benchmarks;

/// <summary>
/// A bare encode-in-a-loop driver for attaching a sampling profiler to one AVIF encode scenario, invoked as
/// <c>PeachImage.Benchmarks.exe avif-encode-profile &lt;scenario&gt; &lt;lossy|lossless&gt; [iterations]</c>.
/// Mirrors <see cref="AvifProfileHarness"/>'s reasoning exactly: BenchmarkDotNet's own pilot/warmup/overhead
/// machinery would interleave engine frames with the frames under study, so this runs exactly one encode
/// call in a tight loop instead. The <c>lossy</c>/<c>lossless</c> mode selects entirely different search
/// paths (DCT/ADST RDO vs. WHT/IntraBC/palette), so both need their own profile run.
/// </summary>
internal static class AvifEncodeProfileHarness
{
    private const int DefaultIterations = 20;

    private static readonly Dictionary<string, string> Scenarios = new(StringComparer.OrdinalIgnoreCase)
    {
        ["photo420"] = "photo_1920x1080_420.avif",
        ["small"] = "small_32x24.avif",
    };

    private static readonly Dictionary<string, AvifEncoderOptions> Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lossy"] = new AvifEncoderOptions(),
        ["lossless"] = new AvifEncoderOptions { Lossless = true },
    };

    public static int Run(string[] args)
    {
        if (args.Length < 3
            || !Scenarios.TryGetValue(args[1], out string? assetName)
            || !Modes.TryGetValue(args[2], out AvifEncoderOptions? options))
        {
            Console.Error.WriteLine(
                $"usage: avif-encode-profile <{string.Join('|', Scenarios.Keys)}> <{string.Join('|', Modes.Keys)}> [iterations]");
            return 1;
        }

        int iterations = args.Length >= 4 && int.TryParse(args[3], out int parsed) && parsed > 0
            ? parsed
            : DefaultIterations;

        string assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", assetName);
        Image image;
        using (var stream = File.OpenRead(assetPath))
        {
            image = AvifDecoder.Decode(stream);
        }

        long sink = EncodeOnce(image, options);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            sink ^= EncodeOnce(image, options);
        }

        stopwatch.Stop();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Console.WriteLine($"{args[1]}/{args[2]}: {iterations} iterations in {stopwatch.Elapsed.TotalMilliseconds:F1} ms " +
                          $"({stopwatch.Elapsed.TotalMilliseconds / iterations:F3} ms/iteration)");
        Console.WriteLine($"allocated: {allocatedBytes:N0} bytes total ({allocatedBytes / (double)iterations / 1024.0:F1} KB/iteration)");
        Console.WriteLine($"checksum: {sink:X}");
        return 0;
    }

    private static long EncodeOnce(Image image, AvifEncoderOptions options)
    {
        using var stream = new MemoryStream();
        AvifEncoder.Encode(image, stream, options);
        return stream.Length;
    }
}
