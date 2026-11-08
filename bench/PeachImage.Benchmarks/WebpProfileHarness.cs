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
        if (args.Length >= 2 && args[1].Equals("simdcpu", StringComparison.OrdinalIgnoreCase))
        {
            // CPU time vs wall time of SimdCodecs' decode of the given asset: > 1 means it uses more than one thread.
            byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", args[2]));
            byte[] rgba = new byte[4032 * 3024 * 4];
            for (int i = 0; i < 5; i++)
            {
                Sdcb.SimdCodecs.SimdCodecs.TryDecodeIntoRgba8(file, rgba);
            }

            var proc = Process.GetCurrentProcess();
            var cpu0 = proc.TotalProcessorTime;
            var sw3 = Stopwatch.StartNew();
            const int Reps = 50;
            for (int i = 0; i < Reps; i++)
            {
                Sdcb.SimdCodecs.SimdCodecs.TryDecodeIntoRgba8(file, rgba);
            }

            sw3.Stop();
            proc.Refresh();
            Console.WriteLine($"wall {sw3.Elapsed.TotalMilliseconds / Reps:F2} ms, cpu {(proc.TotalProcessorTime - cpu0).TotalMilliseconds / Reps:F2} ms per decode");
            return 0;
        }

        if (args.Length >= 2 && args[1].Equals("jpeghash", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Assets"), "*.jpg").OrderBy(f => f, StringComparer.Ordinal))
            {
                using var ms = new MemoryStream(File.ReadAllBytes(path));
                using var img = PeachImage.Formats.Jpeg.JpegDecoder.Decode(ms);
                Console.WriteLine($"{Path.GetFileName(path)}: {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(img.GetPixelSpan()))[..16]}");
            }

            return 0;
        }

        if (args.Length >= 2 && args[1].Equals("jpegphase", StringComparison.OrdinalIgnoreCase))
        {
            byte[] jpegBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", args.Length >= 3 ? args[2] : "photo_1920x1080_420.jpg"));
            double entropyMs = 0, seqMs = 0, parMs = 0;
            const int N = 100;
            for (int i = -5; i < N; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                using var ms = new MemoryStream(jpegBytes);
                var frame = PeachImage.Formats.Jpeg.Decoding.FrameDecoder.Decode(ms);
                long t1 = Stopwatch.GetTimestamp();
                using var a = PeachImage.Formats.Jpeg.Decoding.FrameReconstructor.Reconstruct(frame, null, new PeachImage.Formats.Shared.Parallelism.ParallelSettings(1, null));
                long t2 = Stopwatch.GetTimestamp();
                using var b = PeachImage.Formats.Jpeg.Decoding.FrameReconstructor.Reconstruct(frame, null, default);
                long t3 = Stopwatch.GetTimestamp();
                foreach (var c in frame.Components)
                {
                    c.Coefficients.Return();
                }

                if (i >= 0)
                {
                    entropyMs += (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                    seqMs += (t2 - t1) * 1000.0 / Stopwatch.Frequency;
                    parMs += (t3 - t2) * 1000.0 / Stopwatch.Frequency;
                }
            }

            Console.WriteLine($"entropy {entropyMs / N:F2} ms, reconstruct sequential {seqMs / N:F2} ms, reconstruct default-parallel {parMs / N:F2} ms");
            return 0;
        }

        if (args.Length >= 2 && args[1].StartsWith("jpeg", StringComparison.OrdinalIgnoreCase))
        {
            // jpeg420 | jpeg444 | jpeg12mp | jpeggray [iterations]: a bare JPEG decode loop for a sampling profiler.
            string name = args[1].ToLowerInvariant() switch
            {
                "jpeg444" => "photo_1920x1080_444.jpg",
                "jpeg12mp" => "photo_4032x3024_420.jpg",
                "jpeggray" => "gray_1920x1080.jpg",
                _ => "photo_1920x1080_420.jpg",
            };
            byte[] jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", name));
            int jpegIterations = args.Length >= 3 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 300;
            long jpegSink = 0;
            for (int i = -5; i < jpegIterations; i++)
            {
                if (i == 0)
                {
                    jpegSink = 0;
                }

                using var ms = new MemoryStream(jpeg);
                using var img = PeachImage.Formats.Jpeg.JpegDecoder.Decode(ms);
                jpegSink += img.Width;
            }

            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < jpegIterations; i++)
            {
                using var ms = new MemoryStream(jpeg);
                using var img = PeachImage.Formats.Jpeg.JpegDecoder.Decode(ms);
                jpegSink += img.Width;
            }

            Console.WriteLine($"{args[1]}: {sw2.Elapsed.TotalMilliseconds / jpegIterations:F3} ms/iteration ({jpegSink})");
            return 0;
        }

        if (args.Length >= 2 && args[1].Equals("bool", StringComparison.OrdinalIgnoreCase))
        {
            // Raw bool-decoder throughput on a random stream with a mid probability, branching on every bit.
            var rng = new Random(1);
            byte[] data = new byte[1 << 22];
            rng.NextBytes(data);
            int prob = args.Length >= 3 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 128;
            foreach (int round in new[] { 0, 1, 2 })
            {
                var br = new PeachImage.Formats.Webp.Decoding.Vp8.Vp8BoolDecoder(data, 0, data.Length);
                var st = br.Load();
                int ones = 0;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 20_000_000; i++)
                {
                    if (br.GetBit(ref st, prob) != 0)
                    {
                        ones += 3;
                    }
                    else
                    {
                        ones ^= 1;
                    }
                }

                Console.WriteLine($"prob {prob}: {sw.Elapsed.TotalMilliseconds * 1e6 / 20_000_000:F2} ns/decode (ones={ones})");
            }

            return 0;
        }

        if (args.Length >= 2 && args[1].Equals("hash", StringComparison.OrdinalIgnoreCase))
        {
            // Prints a full-pixel SHA-256 per scenario, so an optimization can be checked for bit-identical output.
            foreach (var (name, asset) in Scenarios)
            {
                byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", asset));
                using var s = new MemoryStream(file);
                var img = WebpDecoder.Decode(s);
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(img.GetPixelSpan()))[..16];
                Console.WriteLine($"{name}: {hash}");
            }

            return 0;
        }

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
        using var image = WebpDecoder.Decode(stream);

        var pixels = image.GetPixelSpan();
        long accumulator = pixels.Length;
        accumulator ^= pixels[0];
        accumulator ^= (long)pixels[pixels.Length / 2] << 8;
        accumulator ^= (long)pixels[^1] << 16;
        return accumulator;
    }
}
