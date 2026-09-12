using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using PeachImage.Formats.Avif;
using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>One baseline record: real <c>aomenc</c>'s own reference lossy OBU byte count for a given corpus file at <see cref="AvifLibaomEncodeLossyParityBaseline.Quality"/>, alongside PeachImage's own OBU byte count for the identical input/quality at the time this record was recorded.</summary>
/// <param name="InputHash">Hash of the raw corpus input file's bytes, so a changed *fixture* is distinguishable from a changed encoder.</param>
/// <param name="Result"><see cref="AvifLibaomEncodeLossyParityBaseline.SkippedMarker"/>, or <c>referenceBytes:actualBytes</c>.</param>
internal sealed record AvifLibaomEncodeLossyParityRecord(string InputHash, string Result);

/// <summary>
/// The lossy sibling of <see cref="AvifLibaomEncodeParityBaseline"/> -- same overall shape and same
/// exact-self-consistency philosophy (see that class's own remarks in full; not repeated here), but for
/// PeachImage's own non-lossless encode path, which this project's own "compare-avif-encoding-to-lucky-
/// clover.md" round log and "for-the-attached-image-nifty-kahan.md" master plan both flag as having no
/// standing corpus-wide comparison harness at all before this class -- Phase 0's own harness (and this
/// baseline's own lossless sibling) were built lossless-first.
///
/// <para><b>Not a byte-identity assertion</b> (unlike the lossless sibling, where identical settings really
/// do produce comparable output): PeachImage's own lossy encode path is independently known and documented
/// to be substantially less mature than libaom's real one (partition search restricted to None/Split only,
/// no tx-size RDO, no quantizer-matrix support, coarser deblock/CDEF -- see the master plan's own lossy
/// scope inventory), so a real, large byte-count gap against <c>aomenc</c> is expected, not a bug this
/// harness exists to flag. What this harness actually guards, exactly like its lossless sibling: PeachImage's
/// own byte count for each corpus file, at the fixed <see cref="Quality"/> below, must not silently drift
/// from what was recorded at baseline time -- any change (improvement or regression) has to be a deliberate,
/// reviewed one, while the real <c>aomenc</c> reference byte count stays visible in the same file as the
/// actual, tracked lossy byte-gap-vs-libaom metric for whoever picks up the real lossy rebuild (project
/// plan's own Phase 4).</para>
///
/// <para>Locks both sides to the exact same real AV1 qindex rather than comparing at "the same quality
/// number" (the two encoders' quality scales are unrelated) -- real <c>aomenc</c>'s own <c>--min-q</c>/
/// <c>--max-q</c> under <c>--end-usage=q</c> are on the real 0-63 quantizer/cq-level scale, not the raw
/// 0-255 qindex (confirmed empirically: passing the raw qindex there fails with "rc_max_quantizer out of
/// range [..63]"), so <see cref="Av1ForwardQuantizer.QualityToQuantizer"/> -- the same first stage
/// <see cref="Av1ForwardQuantizer.QualityToBaseQIdx"/> itself uses -- gives both sides the identical real
/// libaom quantizer-to-qindex mapping with no ad hoc scale translation of this harness's own invention.</para>
/// </summary>
internal static class AvifLibaomEncodeLossyParityBaseline
{
    /// <summary>Recorded in place of a byte-count pair for a corpus file outside this encoder's current pixel-format/size scope, or one <c>aomenc</c> itself couldn't encode.</summary>
    public const string SkippedMarker = "SKIPPED";

    /// <summary>The effort/speed level every recorded comparison uses -- matches the lossless sibling and this project's own corpus-verification convention throughout the round log.</summary>
    private const int Effort = 2;

    /// <summary>
    /// The single fixed quality every recorded comparison uses -- matches this project's own established
    /// placeholder-quality convention elsewhere (e.g. every lossless call site's own unused <c>quality: 75</c>
    /// argument). One quality point keeps this v1 harness's own runtime and baseline size comparable to its
    /// lossless sibling; widening to a real quality sweep is a natural, independent follow-up once this
    /// single point is itself a proven, stable pattern.
    /// </summary>
    private const int Quality = 75;

    /// <summary>Set to <c>write</c> to regenerate <see cref="BaselinePath"/> instead of asserting against it. Requires a local <c>aomenc.exe</c> build (default path below, or <c>PEACHIMAGE_AOMENC_PATH</c>) -- normal (non-write-mode) test runs never invoke it.</summary>
    public const string WriteModeVariable = "PEACHIMAGE_AVIF_LIBAOM_LOSSY_BASELINE";

    /// <summary>Overrides the default local <c>aomenc.exe</c> path (same default this repo's own <c>PeachImage.LibaomParity</c> tool and the lossless sibling baseline use) -- only consulted in write mode.</summary>
    public const string AomencPathVariable = "PEACHIMAGE_AOMENC_PATH";

    private const string DefaultAomencPath = @"C:\Sources\GoogleSource\aom\build_ninja2\aomenc.exe";

    public static string BaselinePath { get; } = Path.Combine(
        RepoRoot, "tests", "PeachImage.Tests", "Formats", "Avif", "Corpus", "AvifLibaomEncodeLossyParity.baseline.tsv");

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

    /// <summary>Every input the baseline covers -- delegates to the lossless sibling's own identical enumeration (same real corpus, same key convention) rather than duplicating it.</summary>
    public static SortedDictionary<string, string> EnumerateInputs() => AvifLibaomEncodeParityBaseline.EnumerateInputs();

    /// <summary>
    /// Decodes <paramref name="path"/> via this project's own decoder, re-encodes the result both via real
    /// <c>aomenc</c> (locked to <see cref="Quality"/>'s own real qindex, constant-quantizer mode) and via
    /// PeachImage's own lossy encoder at the identical qindex, and reduces both to their own OBU byte
    /// counts. Never throws -- any failure (unsupported pixel format, degenerate size, <c>aomenc</c> not
    /// found/failing) records <see cref="SkippedMarker"/> instead.
    /// </summary>
    public static AvifLibaomEncodeLossyParityRecord Compute(string path)
    {
        byte[] fileBytes = File.ReadAllBytes(path);
        string inputHash = Convert.ToHexString(XxHash128.Hash(fileBytes));

        try
        {
            using var stream = new MemoryStream(fileBytes);
            using var image = AvifDecoder.Decode(stream);

            // Rgb24-only, matching the lossless sibling's own current scope.
            if (image.PixelFormat != PixelFormat.Rgb24 || image.Width <= 0 || image.Height <= 0)
            {
                return new AvifLibaomEncodeLossyParityRecord(inputHash, SkippedMarker);
            }

            byte[] rgb = image.GetPixelSpan().ToArray();

            string aomencPath = Environment.GetEnvironmentVariable(AomencPathVariable) ?? DefaultAomencPath;
            if (!File.Exists(aomencPath))
            {
                return new AvifLibaomEncodeLossyParityRecord(inputHash, SkippedMarker);
            }

            var (y, u, v) = Av1RgbToYuvIdentityConverter.Convert(rgb, image.Width, image.Height);

            string tempDir = Path.Combine(Path.GetTempPath(), $"peachimage-avif-libaom-lossy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                string y4mPath = Path.Combine(tempDir, "input.y4m");
                WriteY4m(y4mPath, y, u, v, image.Width, image.Height);

                string obuPath = Path.Combine(tempDir, "reference.obu");
                int quantizer = Av1ForwardQuantizer.QualityToQuantizer(Quality);
                if (!TryRunAomencLossy(aomencPath, y4mPath, obuPath, Effort, quantizer))
                {
                    return new AvifLibaomEncodeLossyParityRecord(inputHash, SkippedMarker);
                }

                int referenceBytes = (int)new FileInfo(obuPath).Length;
                int actualBytes = Av1FrameEncoder.Encode(rgb, image.Width, image.Height, monoChrome: false, Quality, lossless: false, Effort).ObuBytes.Length;

                return new AvifLibaomEncodeLossyParityRecord(inputHash, $"{referenceBytes}:{actualBytes}");
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
            return new AvifLibaomEncodeLossyParityRecord(inputHash, SkippedMarker);
        }
    }

    /// <summary>See <c>PeachImage.LibaomParity.Program.RunAomenc</c>'s sibling invocation -- kept in sync by hand (this test project deliberately has no reference to that console-app project, matching every other self-contained baseline in this directory). Locked to a real, fixed <paramref name="quantizer"/> (0-63 scale) via <c>--end-usage=q --min-q=N --max-q=N</c> instead of <c>--lossless=1</c>.</summary>
    private static bool TryRunAomencLossy(string aomencPath, string y4mPath, string obuPath, int effort, int quantizer)
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
        startInfo.ArgumentList.Add("--end-usage=q");
        startInfo.ArgumentList.Add($"--min-q={quantizer}");
        startInfo.ArgumentList.Add($"--max-q={quantizer}");
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

            // aomenc warns interactively ("Bad quantizer values... Continue? (y to continue)") when
            // min-q == max-q, even though that's exactly what a real fixed-qindex constant-quantizer encode
            // needs -- auto-confirm rather than widening the range, since widening would let its own rate
            // control drift off the exact qindex this comparison depends on.
            process.StandardInput.WriteLine("y");
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

    /// <summary>Same hang-protection rationale as the lossless sibling's own identical field.</summary>
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
    public static Dictionary<string, AvifLibaomEncodeLossyParityRecord> Load()
    {
        var records = new Dictionary<string, AvifLibaomEncodeLossyParityRecord>(StringComparer.Ordinal);
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
                records[fields[0]] = new AvifLibaomEncodeLossyParityRecord(fields[1], fields[2]);
            }
        }

        return records;
    }

    /// <summary>Rewrites the baseline from freshly computed records.</summary>
    public static void Save(SortedDictionary<string, AvifLibaomEncodeLossyParityRecord> records)
    {
        using var writer = new StreamWriter(BaselinePath);
        writer.NewLine = "\n";
        writer.WriteLine("# AVIF lossy encode/aomenc byte-gap baseline: key<TAB>inputHash<TAB>referenceBytes:actualBytes (or SKIPPED).");
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"# referenceBytes is real aomenc's own lossy OBU byte count (effort {Effort}, quality {Quality}, locked to the same real AV1 qindex on both sides via Av1ForwardQuantizer.QualityToQuantizer); actualBytes is PeachImage's own, recorded together so the standing test can assert exact self-consistency without needing aomenc present. This is NOT a byte-identity target -- PeachImage's own lossy path is independently known to be far less mature than libaom's real one (see AvifLibaomEncodeLossyParityBaseline's own class remarks) -- the real, tracked metric here is the aggregate gap below, watched over time as the real lossy rebuild (project plan Phase 4) lands. Regenerate with {WriteModeVariable}=write dotnet test --filter AvifLibaomEncodeLossyParityTests (requires a local aomenc.exe -- see {AomencPathVariable}), then review the diff."));

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
