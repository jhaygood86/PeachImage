using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using PeachImage.Formats.Avif;
using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// One baseline record: real <c>aomenc</c>'s own reference OBU byte count for a given corpus file, alongside
/// PeachImage's own OBU byte count for the identical input at the time this record was recorded.
/// </summary>
/// <param name="InputHash">Hash of the raw corpus input file's bytes, so a changed *fixture* is distinguishable from a changed encoder.</param>
/// <param name="Result"><see cref="AvifLibaomEncodeParityBaseline.SkippedMarker"/>, or <c>referenceBytes:actualBytes</c>.</param>
internal sealed record AvifLibaomEncodeParityRecord(string InputHash, string Result);

/// <summary>
/// Reads, writes, and computes the AVIF encode/<c>aomenc</c> byte-gap baseline -- the standing-test
/// counterpart to <c>tools/PeachImage.LibaomParity</c>'s own <c>--corpus</c> mode (project plan
/// "compare-avif-encoding-to-lucky-clover.md", Phase 0's own "verification infrastructure" item, and the
/// master plan's own definition-of-done item 3: "every image in the existing AVIF corpus... re-encoded via
/// both real aomenc and PeachImage at matching settings... is byte-identical" as a permanent, standing test,
/// not a one-off manual tool run). Same overall shape as <see cref="AvifFfmpegReferenceBaseline"/> (baseline
/// TSV, write-mode env var, timeout-guarded external-process invocation) -- and the same reason for storing a
/// *frozen* reference value rather than re-running the external tool every test: <c>aomenc</c> is not
/// guaranteed to be on every machine/CI runner this test suite runs on, so the checked-in baseline lets the
/// standing test assert PeachImage's own current byte count against a frozen `aomenc` snapshot without
/// needing `aomenc` present at ordinary (non-write-mode) test-run time.
///
/// <para>Unlike <see cref="AvifFfmpegReferenceBaseline"/> (which stores only the external reference, since the
/// comparison test re-decodes PeachImage's own current output fresh every run), this baseline also freezes
/// PeachImage's own byte count *at recording time* -- so the standing test becomes an exact self-consistency
/// regression detector (mirroring <see cref="AvifDecodeHashBaseline"/>'s own philosophy: "did PeachImage's own
/// output change", catching every encoder behavior change, improvement or regression alike, for deliberate
/// review) while the checked-in `aomenc` reference byte count remains visible in the same file, in git history,
/// as the real, tracked byte-gap-vs-libaom metric the master plan's own definition of done cares about.</para>
/// </summary>
internal static class AvifLibaomEncodeParityBaseline
{
    /// <summary>Recorded in place of a byte-count pair for a corpus file outside this encoder's current pixel-format/size scope, or one <c>aomenc</c> itself couldn't encode.</summary>
    public const string SkippedMarker = "SKIPPED";

    /// <summary>The lossless effort/speed level every recorded comparison uses -- matches <c>PeachImage.LibaomParity</c>'s own default and this project's own corpus-verification convention throughout the round log.</summary>
    private const int Effort = 2;

    /// <summary>Set to <c>write</c> to regenerate <see cref="BaselinePath"/> instead of asserting against it. Requires a local <c>aomenc.exe</c> build (default path below, or <c>PEACHIMAGE_AOMENC_PATH</c>) -- normal (non-write-mode) test runs never invoke it.</summary>
    public const string WriteModeVariable = "PEACHIMAGE_AVIF_LIBAOM_BASELINE";

    /// <summary>Overrides the default local <c>aomenc.exe</c> path (same default this repo's own <c>PeachImage.LibaomParity</c> tool uses) -- only consulted in write mode.</summary>
    public const string AomencPathVariable = "PEACHIMAGE_AOMENC_PATH";

    private const string DefaultAomencPath = @"C:\Sources\GoogleSource\aom\build_ninja2\aomenc.exe";

    public static string BaselinePath { get; } = Path.Combine(
        RepoRoot, "tests", "PeachImage.Tests", "Formats", "Avif", "Corpus", "AvifLibaomEncodeParity.baseline.tsv");

    public static bool IsWriteMode =>
        string.Equals(Environment.GetEnvironmentVariable(WriteModeVariable), "write", StringComparison.OrdinalIgnoreCase);

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeachImage.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? AppContext.BaseDirectory;
        }
    }

    /// <summary>Every input the baseline covers: the full libavif <c>tests/data</c> corpus, same set every other corpus baseline in this directory uses.</summary>
    public static SortedDictionary<string, string> EnumerateInputs()
    {
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (Directory.Exists(CorpusPaths.LibavifTestDataRoot))
        {
            foreach (var file in Directory.EnumerateFiles(CorpusPaths.LibavifTestDataRoot, "*.avif", SearchOption.AllDirectories))
            {
                inputs["corpus/" + Path.GetRelativePath(CorpusPaths.LibavifTestDataRoot, file).Replace('\\', '/')] = file;
            }
        }

        return inputs;
    }

    /// <summary>
    /// Decodes <paramref name="path"/> via this project's own decoder, re-encodes the result both via real
    /// <c>aomenc</c> and via PeachImage's own lossless encoder (identical settings: effort 2, lossless,
    /// 4:4:4/identity-matrix -- matching <c>PeachImage.LibaomParity --corpus</c>'s own invocation exactly),
    /// and reduces both to their own OBU byte counts. Never throws -- any failure (unsupported pixel format,
    /// degenerate size, <c>aomenc</c> not found/failing) records <see cref="SkippedMarker"/> instead.
    /// </summary>
    public static AvifLibaomEncodeParityRecord Compute(string path)
    {
        byte[] fileBytes = File.ReadAllBytes(path);
        string inputHash = Convert.ToHexString(XxHash128.Hash(fileBytes));

        try
        {
            using var stream = new MemoryStream(fileBytes);
            using var image = AvifDecoder.Decode(stream);

            // Rgb24-only, matching PeachImage.LibaomParity's own current scope -- Gray8 would need
            // Av1FrameEncoder.Encode's own monoChrome path wired up in this harness too (not yet done), and
            // higher-bit-depth/alpha formats aren't a pixel shape this encoder round-trips through
            // Av1RgbToYuvIdentityConverter at all.
            if (image.PixelFormat != PixelFormat.Rgb24 || image.Width <= 0 || image.Height <= 0)
            {
                return new AvifLibaomEncodeParityRecord(inputHash, SkippedMarker);
            }

            byte[] rgb = image.GetPixelSpan().ToArray();

            string aomencPath = Environment.GetEnvironmentVariable(AomencPathVariable) ?? DefaultAomencPath;
            if (!File.Exists(aomencPath))
            {
                return new AvifLibaomEncodeParityRecord(inputHash, SkippedMarker);
            }

            var (y, u, v) = Av1RgbToYuvIdentityConverter.Convert(rgb, image.Width, image.Height);

            string tempDir = Path.Combine(Path.GetTempPath(), $"peachimage-avif-libaom-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                string y4mPath = Path.Combine(tempDir, "input.y4m");
                WriteY4m(y4mPath, y, u, v, image.Width, image.Height);

                string obuPath = Path.Combine(tempDir, "reference.obu");
                if (!TryRunAomenc(aomencPath, y4mPath, obuPath, Effort))
                {
                    return new AvifLibaomEncodeParityRecord(inputHash, SkippedMarker);
                }

                int referenceBytes = (int)new FileInfo(obuPath).Length;
                int actualBytes = Av1FrameEncoder.Encode(rgb, image.Width, image.Height, monoChrome: false, quality: 75, lossless: true, Effort).ObuBytes.Length;

                return new AvifLibaomEncodeParityRecord(inputHash, $"{referenceBytes}:{actualBytes}");
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
        catch
        {
            return new AvifLibaomEncodeParityRecord(inputHash, SkippedMarker);
        }
    }

    /// <summary>See <c>PeachImage.LibaomParity.Program.RunAomenc</c>'s identical invocation -- kept in sync by hand (this test project deliberately has no reference to that console-app project, matching every other self-contained baseline in this directory).</summary>
    private static bool TryRunAomenc(string aomencPath, string y4mPath, string obuPath, int effort)
    {
        var startInfo = new ProcessStartInfo(aomencPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(y4mPath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(obuPath);
        startInfo.ArgumentList.Add("--obu");
        startInfo.ArgumentList.Add("--i444");
        startInfo.ArgumentList.Add("--lossless=1");
        startInfo.ArgumentList.Add("--matrix-coefficients=identity");
        startInfo.ArgumentList.Add("--color-primaries=bt709");
        startInfo.ArgumentList.Add("--transfer-characteristics=srgb");
        startInfo.ArgumentList.Add("--usage=2");
        startInfo.ArgumentList.Add("--limit=1");
        startInfo.ArgumentList.Add($"--cpu-used={effort}");
        startInfo.ArgumentList.Add("--tile-columns=0");
        startInfo.ArgumentList.Add("--tile-rows=0");
        startInfo.ArgumentList.Add("--sb-size=128");
        startInfo.ArgumentList.Add("--bit-depth=8");
        startInfo.ArgumentList.Add("--passes=1");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ProcessTimeout))
            {
                TryKill(process);
                return false;
            }

            stdoutTask.Wait(TimeSpan.FromSeconds(5));
            stderrTask.Wait(TimeSpan.FromSeconds(5));

            return process.ExitCode == 0 && File.Exists(obuPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Same hang-protection rationale as <c>AvifFfmpegReferenceBaseline</c>'s identical field -- a lossless encode of the largest corpus file is comfortably under this even at effort 0; this is a safety net, not a tuned budget.</summary>
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void WriteY4m(string path, int[] y, int[] u, int[] v, int width, int height)
    {
        using var stream = File.Create(path);
        byte[] header = Encoding.ASCII.GetBytes($"YUV4MPEG2 W{width} H{height} F25:1 Ip A0:0 C444 XCOLORRANGE=FULL\nFRAME\n");
        stream.Write(header);
        WritePlane(stream, y, width, height);
        WritePlane(stream, u, width, height);
        WritePlane(stream, v, width, height);
    }

    private static void WritePlane(Stream stream, int[] plane, int width, int height)
    {
        var buffer = new byte[width * height];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)plane[i];
        }

        stream.Write(buffer);
    }

    /// <summary>Loads the checked-in baseline, or an empty map when it doesn't exist yet.</summary>
    public static Dictionary<string, AvifLibaomEncodeParityRecord> Load()
    {
        var records = new Dictionary<string, AvifLibaomEncodeParityRecord>(StringComparer.Ordinal);
        if (!File.Exists(BaselinePath))
        {
            return records;
        }

        foreach (string line in File.ReadLines(BaselinePath))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] fields = line.Split('\t');
            if (fields.Length == 3)
            {
                records[fields[0]] = new AvifLibaomEncodeParityRecord(fields[1], fields[2]);
            }
        }

        return records;
    }

    /// <summary>Rewrites the baseline from freshly computed records.</summary>
    public static void Save(SortedDictionary<string, AvifLibaomEncodeParityRecord> records)
    {
        using var writer = new StreamWriter(BaselinePath);
        writer.NewLine = "\n";
        writer.WriteLine("# AVIF encode/aomenc byte-gap baseline: key<TAB>inputHash<TAB>referenceBytes:actualBytes (or SKIPPED).");
        writer.WriteLine($"# referenceBytes is real aomenc's own lossless OBU byte count (effort {Effort}); actualBytes is PeachImage's own, recorded together so the standing test can assert exact self-consistency without needing aomenc present. Regenerate with {WriteModeVariable}=write dotnet test --filter AvifLibaomEncodeParityTests (requires a local aomenc.exe -- see {AomencPathVariable}), then review the diff.");

        long totalReference = 0;
        long totalActual = 0;
        foreach (var (key, record) in records)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key}\t{record.InputHash}\t{record.Result}"));
            if (record.Result != SkippedMarker)
            {
                string[] parts = record.Result.Split(':');
                if (parts.Length == 2
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int reference)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int actual))
                {
                    totalReference += reference;
                    totalActual += actual;
                }
            }
        }

        if (totalReference > 0)
        {
            double gapPct = ((double)totalActual - totalReference) / totalReference * 100.0;
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"# Aggregate at recording time: reference={totalReference} actual={totalActual} ({gapPct:+0.00;-0.00}%)."));
        }
    }
}
