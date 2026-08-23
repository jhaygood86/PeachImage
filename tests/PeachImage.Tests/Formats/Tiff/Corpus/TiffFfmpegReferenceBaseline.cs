using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;

namespace PeachImage.Tests.Formats.Tiff.Corpus;

/// <summary>One reference record: what <c>ffmpeg</c>'s independent TIFF decoder produced for a given input file, reduced to a hash.</summary>
/// <param name="InputHash">Hash of the raw input file's bytes, so a changed *fixture* is distinguishable from a changed reference.</param>
/// <param name="Result">Either <see cref="TiffFfmpegReferenceBaseline.SkippedMarker"/> (ffmpeg itself couldn't decode this file, or a dimensions mismatch made the raw output untrustworthy) or <c>WxH:pixelHash</c>.</param>
internal sealed record TiffFfmpegReferenceRecord(string InputHash, string Result);

/// <summary>
/// Reads, writes, and computes the TIFF/<c>ffmpeg</c> reference baseline — a flat record of what
/// <c>ffmpeg</c>'s own, independent TIFF decoder (<c>libavcodec/tiff.c</c>) produces for every corpus file it
/// can decode, in the canonical 8-bit RGBA layout <c>-pix_fmt rgba</c> uses. Unlike <c>AvifDecodeHashBaseline</c>/
/// <c>WebpDecodeHashBaseline</c> (which hash a PeachImage decoder's own prior output — a pure regression
/// detector), this is generated from a genuinely independent decoder, so <c>TiffFfmpegReferenceTests</c>
/// comparing against it is a real correctness check, not just a "did anything change" snapshot.
/// </summary>
/// <remarks>
/// The canonical format is deliberately 8-bit RGBA, not a wider 16-bit format: empirically verified (by
/// comparing raw <c>ffmpeg</c> output at both <c>-pix_fmt rgba</c> and <c>-pix_fmt rgba64le</c> for the same
/// 8-bit-source file) that <c>ffmpeg</c>'s 8-to-16-bit upscale does <em>not</em> use the simple
/// bit-replication (<c>v*257</c>) or plain left-shift (<c>v&lt;&lt;8</c>) formula this codebase's own
/// conversions use — it goes through <c>libswscale</c>'s general-purpose scaling, whose exact output isn't a
/// value worth trying to replicate bit-for-bit in C#. Staying at 8-bit RGBA sidesteps that entirely: TIFF's
/// grayscale-&gt;RGBA and RGB-&gt;RGBA expansions are plain channel broadcasts/passthroughs with no
/// precision-changing math on either side, so they compare exactly. This does mean 16-bit-source TIFFs
/// aren't covered by this particular check (only by the broader graceful-decode corpus test and the
/// hand-authored 16-bit unit tests) — a deliberate, documented scope trim, not an oversight.
/// </remarks>
internal static class TiffFfmpegReferenceBaseline
{
    /// <summary>Recorded in place of a pixel hash when <c>ffmpeg</c> itself can't decode this file (or its output doesn't match its own reported dimensions).</summary>
    public const string SkippedMarker = "SKIPPED";

    /// <summary>Set to <c>write</c> to regenerate <see cref="BaselinePath"/> instead of asserting against it. Requires <c>ffmpeg</c>/<c>ffprobe</c> on PATH — normal (non-write-mode) test runs never invoke either.</summary>
    public const string WriteModeVariable = "PEACHIMAGE_TIFF_FFMPEG_BASELINE";

    public static string BaselinePath { get; } = Path.Combine(
        RepoRoot, "tests", "PeachImage.Tests", "Formats", "Tiff", "Corpus", "TiffFfmpegReference.baseline.tsv");

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

    /// <summary>Every input the baseline covers: the <c>valid</c> and <c>edge-cases</c> corpus subsets (not <c>robustness</c> — those are malformed on purpose, and ffmpeg's behavior on them isn't a meaningful correctness signal).</summary>
    public static SortedDictionary<string, string> EnumerateInputs()
    {
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (string bucket in (string[])["valid", "edge-cases"])
        {
            string directory = Path.Combine(CorpusPaths.ImazenRoot, "tiff-conformance", bucket);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                inputs[$"{bucket}/{Path.GetFileName(file)}"] = file;
            }
        }

        return inputs;
    }

    /// <summary>Runs <c>ffmpeg</c>/<c>ffprobe</c> against <paramref name="path"/> and reduces the result to a baseline record. Never throws.</summary>
    public static TiffFfmpegReferenceRecord Compute(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string inputHash = Convert.ToHexString(XxHash128.Hash(bytes));

        string tempFile = Path.Combine(Path.GetTempPath(), $"peachimage-tiff-ffmpeg-{Guid.NewGuid():N}.raw");
        try
        {
            bool ran = TryRunProcess("ffmpeg", ["-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgba", "-y", tempFile], out _, out string ffmpegStderr);

            // ffmpeg's TIFF decoder has at least one case (old-style, pre-TIFF-6.0 LZW -- confirmed
            // empirically against "quad-lzw.tif") where it logs an error-level message ("Old style LZW is
            // unsupported... Decoded only N bytes of M") but still exits 0 and still writes a raw file, just
            // one silently padded with garbage/zero for whatever it couldn't decode. At `-v error` verbosity
            // a genuinely clean decode prints nothing at all, so *any* stderr output -- not just a nonzero
            // exit code -- means this file's "reference" isn't trustworthy ground truth and must be skipped.
            if (!ran || ffmpegStderr.Length > 0 || !File.Exists(tempFile))
            {
                return new TiffFfmpegReferenceRecord(inputHash, SkippedMarker);
            }

            byte[] raw = File.ReadAllBytes(tempFile);
            var (width, height) = ProbeDimensions(path);

            if (width <= 0 || height <= 0 || raw.Length == 0 || (long)width * height * 4 != raw.Length)
            {
                return new TiffFfmpegReferenceRecord(inputHash, SkippedMarker);
            }

            string pixelHash = Convert.ToHexString(XxHash128.Hash(raw));
            return new TiffFfmpegReferenceRecord(inputHash, $"{width}x{height}:{pixelHash}");
        }
        catch
        {
            return new TiffFfmpegReferenceRecord(inputHash, SkippedMarker);
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException)
            {
            }
        }
    }

    private static (int Width, int Height) ProbeDimensions(string path)
    {
        if (!TryRunProcess("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", path], out string output, out _))
        {
            return (0, 0);
        }

        string[] parts = output.Trim().Split(',');
        return parts.Length >= 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height)
            ? (width, height)
            : (0, 0);
    }

    /// <summary>
    /// A handful of adversarial corpus files (this decoder isn't the only thing a malformed/edge-case TIFF
    /// can trip up) made <c>ffmpeg</c> itself hang outright while generating this baseline the first time —
    /// caught empirically, not theoretically. The fix has two parts, both required: <c>-nostdin</c> so
    /// <c>ffmpeg</c> never blocks waiting for a keypress on an ambiguous prompt, and starting the redirected
    /// stdout/stderr reads *before* bounding the wait on <see cref="Process.WaitForExit(TimeSpan)"/> — a
    /// naive <c>ReadToEnd()</c> called before waiting can itself block forever with no timeout of its own if
    /// the child fills a pipe buffer, which is exactly what happened here. If the process still hasn't
    /// exited within <see cref="ProcessTimeout"/>, it's killed outright rather than left running.
    /// </summary>
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    private static bool TryRunProcess(string fileName, IReadOnlyList<string> arguments, out string standardOutput, out string standardError)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        if (fileName == "ffmpeg")
        {
            // ffprobe doesn't recognize -nostdin the way ffmpeg does (it errors out immediately trying to
            // parse a value for it) -- confirmed empirically, not assumed. ffprobe has no interactive
            // overwrite-style prompt to guard against in the first place, so it doesn't need this.
            startInfo.ArgumentList.Add("-nostdin");
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                standardOutput = string.Empty;
                standardError = string.Empty;
                return false;
            }

            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ProcessTimeout))
            {
                TryKill(process);
                standardOutput = string.Empty;
                standardError = string.Empty;
                return false;
            }

            // The process has exited, so the redirected streams should reach EOF almost immediately; still
            // bounded, rather than trusting that unconditionally.
            standardOutput = stdoutTask.Wait(TimeSpan.FromSeconds(5)) ? stdoutTask.Result : string.Empty;
            standardError = stderrTask.Wait(TimeSpan.FromSeconds(5)) ? stderrTask.Result : string.Empty;

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // ffmpeg/ffprobe not on PATH.
            standardOutput = string.Empty;
            standardError = string.Empty;
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the WaitForExit timeout and here.
        }
    }

    /// <summary>Loads the checked-in baseline, or an empty map when it doesn't exist yet.</summary>
    public static Dictionary<string, TiffFfmpegReferenceRecord> Load()
    {
        var records = new Dictionary<string, TiffFfmpegReferenceRecord>(StringComparer.Ordinal);
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
                records[fields[0]] = new TiffFfmpegReferenceRecord(fields[1], fields[2]);
            }
        }

        return records;
    }

    /// <summary>Rewrites the baseline from freshly computed records.</summary>
    public static void Save(SortedDictionary<string, TiffFfmpegReferenceRecord> records)
    {
        using var writer = new StreamWriter(BaselinePath);
        writer.NewLine = "\n";
        writer.WriteLine("# TIFF/ffmpeg reference baseline: key<TAB>inputHash<TAB>WxH:pixelHash (or SKIPPED).");
        writer.WriteLine($"# Generated from ffmpeg's independent TIFF decoder, 8-bit RGBA. Regenerate with {WriteModeVariable}=write dotnet test --filter TiffFfmpegReferenceTests (requires ffmpeg/ffprobe on PATH), then review the diff.");

        foreach (var (key, record) in records)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key}\t{record.InputHash}\t{record.Result}"));
        }
    }
}
