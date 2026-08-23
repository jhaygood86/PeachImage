using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>One reference record: what <c>ffmpeg</c>'s independent AVIF decoder produced for a given input file, reduced to a bounded pixel sample.</summary>
/// <param name="InputHash">Hash of the raw input file's bytes, so a changed *fixture* is distinguishable from a changed reference.</param>
/// <param name="Result">Either <see cref="AvifFfmpegReferenceBaseline.SkippedMarker"/> or <c>WxH:hexSamples</c> -- see <see cref="AvifFfmpegReferenceBaseline"/>'s remarks for why this stores a bounded sample of pixels rather than a single hash of the whole buffer the way <c>TiffFfmpegReferenceBaseline</c> does.</param>
internal sealed record AvifFfmpegReferenceRecord(string InputHash, string Result);

/// <summary>
/// Reads, writes, and computes the AVIF/<c>ffmpeg</c> reference baseline — a flat record of what <c>ffmpeg</c>'s
/// own, independent AVIF decoder (<c>libdav1d</c> via <c>-c:v libdav1d</c>, the same invocation this repo's own
/// benchmark methodology in <c>LIBRARY_COMPARISON.md</c> uses) produces for every corpus file it can decode
/// unambiguously. Mirrors <c>TiffFfmpegReferenceBaseline</c>'s overall shape (baseline TSV, write-mode env var,
/// same hang/partial-decode-safe <c>ffmpeg</c>/<c>ffprobe</c> invocation) -- this is a genuinely independent
/// decoder, unlike <c>AvifDecodeHashBaseline</c>/<c>WebpDecodeHashBaseline</c>, which hash a PeachImage
/// decoder's own prior output (a pure regression detector). SkiaSharp has no AVIF codec at all, so before this
/// baseline AVIF had no independent decode oracle whatsoever.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a bounded pixel sample, not a single hash of the whole buffer (unlike TIFF).</b> TIFF's comparable
/// pixel formats (Gray8/Rgb24) are plain channel broadcasts/passthroughs with no floating-point math on either
/// side, so an exact hash match is the right tool. AVIF is fundamentally different: converting decoded YUV
/// samples to RGB is a real floating-point matrix operation (<c>Av1YuvToRgbConverter</c>), and empirically,
/// PeachImage's and <c>ffmpeg</c>'s independent implementations of that matrix do not produce bit-identical
/// output -- confirmed by decoding the same tiny 4x4 file (<c>extended_pixi.avif</c>) both ways and diffing
/// raw bytes: every non-clamped channel differs by exactly 1 (e.g. <c>(0,131,0)</c> vs. <c>(0,132,0)</c>),
/// consistent with an ordinary last-bit rounding-order difference between two independently-written
/// floating-point implementations, not a bug (a bug of this class was in fact found and fixed during this
/// investigation -- see below -- and this residual 1-off difference is what's left after that fix). For
/// chroma-subsampled (4:2:0/4:2:2) content the gap is much larger still (up to ~70 on real photographic
/// corpus files like <c>kodim03_yuv420_8bpc.avif</c>/<c>kodim23_yuv420_8bpc.avif</c>): <c>Av1YuvToRgbConverter</c>'s
/// own doc comment already documents that its chroma upsampling is "a scalar-first approximation... not a
/// bilinear or <c>chroma_sample_position</c>-aware reconstruction", while <c>ffmpeg</c>/<c>libdav1d</c> uses a
/// properly interpolated upsampler -- the exact same class of legitimate, already-accepted implementation
/// divergence <c>WebpCorpusTests</c>' <c>CorpusAssertions.MaxChannelTolerance</c> documents for VP8's chroma
/// upsampler (there, up to 6; here, a coarser NN-vs-bilinear gap measures larger). Given both of these are
/// genuine floating-point/algorithm differences rather than bugs, an exact hash is the wrong tool; a
/// mean/max-per-channel tolerance check (<see cref="AvifFfmpegReferenceTests"/>, mirroring
/// <c>CorpusAssertions.AssertWithinTolerance</c>) is used instead. That, in turn, means the baseline needs the
/// actual pixel *values* at comparison time, not just a hash of them -- but storing every pixel of every
/// comparable file (several are 768x512+) would make the checked-in baseline multiple megabytes of hex text,
/// unlike TIFF's compact hash-only rows. Storing a small, deterministic, evenly-spaced sample (up to
/// <see cref="SampleCount"/> pixels) keeps the file diff-friendly while still reliably catching the class of
/// bug this oracle exists to catch: a systematic error (wrong matrix coefficients, wrong color range, a
/// container-level property silently ignored) that's wrong across most or all of an image, not a "wrong only
/// in the last SIMD lane" bug (that's <c>AvifDecodeHashTests</c>' job). This investigation in fact found and
/// fixed exactly the systematic kind from a single 4x4 file's first sampled pixel, before this sampling scheme
/// even existed -- see <c>Av1YuvToRgbConverter</c>'s <c>cRange</c> remark for the confirmed bug (full-range
/// chroma was normalized against half the correct excursion, silently over-saturating every full-range,
/// non-gray AVIF this decoder produced).
/// </para>
/// <para>
/// <b>Why files with an <c>irot</c>/<c>imir</c>/<c>clap</c> item property are excluded.</b> These HEIF
/// transformative properties (rotate/mirror/crop) are silently ignored by this decoder today --
/// <c>AvifItemPropertiesBox.ParseIpma</c>'s own doc comment confirms this is deliberate, existing leniency
/// ("a property we don't recognize is simply ignored... matching this codebase's general leniency toward
/// ancillary/unrecognized data"), not specific to this investigation. <c>ffmpeg</c> does apply them, so
/// comparing against files carrying one is comparing a rotated/mirrored/cropped image against an
/// unrotated/unmirrored/uncropped one -- confirmed empirically (<c>abc_color_irot_alpha_NOirot.avif</c>
/// measured a mean per-channel difference of ~27 and a max of 255, an order of magnitude beyond the
/// legitimate chroma-upsampling gap above). This is a real, silently-wrong-pixels gap worth fixing
/// eventually (this decoder's own README documents throwing <see cref="AvifUnsupportedFeatureException"/>
/// rather than silently producing wrong pixels as the house style for out-of-scope features -- irot/imir/clap
/// don't yet follow that), but implementing spatial transform application (or teaching the leniency path to
/// distinguish pixel-semantic-changing properties from purely informational ones) is a real feature change,
/// not something this differential-test addition should smuggle in. Detected via a plain byte-level scan for
/// the property fourCCs anywhere in the file -- simple and self-contained rather than coupling this test
/// project to <c>AvifItemPropertiesBox</c>'s internals, at the cost of being a heuristic (a false-positive
/// match inside compressed AV1 payload bytes would only ever cause an unnecessary skip, never a false
/// failure).
/// </para>
/// </remarks>
internal static class AvifFfmpegReferenceBaseline
{
    /// <summary>Recorded in place of a pixel sample when <c>ffmpeg</c> itself can't decode this file, a dimensions mismatch made the raw output untrustworthy (see this class's remarks for why that also covers alpha/grid files), or the file carries an unimplemented transformative property (also covered in the remarks).</summary>
    public const string SkippedMarker = "SKIPPED";

    /// <summary>How many evenly-spaced pixels are sampled from a comparable file's raw decode -- see this class's remarks for why a bounded sample, not the whole buffer, is stored.</summary>
    private const int SampleCount = 256;

    /// <summary>Set to <c>write</c> to regenerate <see cref="BaselinePath"/> instead of asserting against it. Requires <c>ffmpeg</c>/<c>ffprobe</c> (with <c>libdav1d</c> support) on PATH — normal (non-write-mode) test runs never invoke either.</summary>
    public const string WriteModeVariable = "PEACHIMAGE_AVIF_FFMPEG_BASELINE";

    public static string BaselinePath { get; } = Path.Combine(
        RepoRoot, "tests", "PeachImage.Tests", "Formats", "Avif", "Corpus", "AvifFfmpegReference.baseline.tsv");

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

    /// <summary>Every input the baseline covers: the full libavif <c>tests/data</c> corpus (the same set <see cref="CorpusFileSource.AvifFiles"/>/<see cref="AvifDecodeHashBaseline"/> use).</summary>
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

    /// <summary>Every evenly-spaced sample position (pixel index, not byte offset) this class samples for a <paramref name="pixelCount"/>-pixel image, in the same deterministic order both the generator and the comparison test use.</summary>
    internal static IEnumerable<int> SamplePositions(int pixelCount)
    {
        if (pixelCount <= 0)
        {
            yield break;
        }

        int sampleCount = Math.Min(SampleCount, pixelCount);
        int stride = Math.Max(1, pixelCount / sampleCount);
        for (int i = 0; i < sampleCount; i++)
        {
            int index = i * stride;
            if (index >= pixelCount)
            {
                break;
            }

            yield return index;
        }
    }

    /// <summary>Runs <c>ffmpeg</c>/<c>ffprobe</c> against <paramref name="path"/> and reduces the result to a baseline record. Never throws.</summary>
    public static AvifFfmpegReferenceRecord Compute(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string inputHash = Convert.ToHexString(XxHash128.Hash(bytes));

        if (HasUnappliedTransformProperty(bytes))
        {
            return new AvifFfmpegReferenceRecord(inputHash, SkippedMarker);
        }

        string tempFile = Path.Combine(Path.GetTempPath(), $"peachimage-avif-ffmpeg-{Guid.NewGuid():N}.raw");
        try
        {
            bool ran = TryRunProcess(
                "ffmpeg",
                ["-v", "error", "-c:v", "libdav1d", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgba", "-y", tempFile],
                out _,
                out string ffmpegStderr);

            // At `-v error` verbosity a genuinely clean single-frame decode prints nothing to stderr at all, so
            // *any* stderr output -- not just a nonzero exit code -- means this file's "reference" isn't
            // trustworthy ground truth (mirrors TiffFfmpegReferenceBaseline's quad-lzw.tif finding: ffmpeg can
            // exit 0 while only partially decoding a file).
            if (!ran || ffmpegStderr.Length > 0 || !File.Exists(tempFile))
            {
                return new AvifFfmpegReferenceRecord(inputHash, SkippedMarker);
            }

            byte[] raw = File.ReadAllBytes(tempFile);
            var (width, height) = ProbeDimensions(path);

            // This equality check is also this class's alpha/grid filter -- see the class remarks for why an
            // alpha-bearing or grid-composited file can never satisfy it.
            if (width <= 0 || height <= 0 || raw.Length == 0 || (long)width * height * 4 != raw.Length)
            {
                return new AvifFfmpegReferenceRecord(inputHash, SkippedMarker);
            }

            string sampleHex = EncodeSamples(raw, width * height);
            return new AvifFfmpegReferenceRecord(inputHash, $"{width}x{height}:{sampleHex}");
        }
        catch
        {
            return new AvifFfmpegReferenceRecord(inputHash, SkippedMarker);
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

    /// <summary>Hex-encodes the RGBA bytes at every <see cref="SamplePositions"/> index of a packed 8-bit-per-channel raw buffer, in position order.</summary>
    internal static string EncodeSamples(ReadOnlySpan<byte> rgba, int pixelCount)
    {
        var builder = new StringBuilder();
        foreach (int index in SamplePositions(pixelCount))
        {
            int offset = index * 4;
            builder.Append(Convert.ToHexString(rgba.Slice(offset, 4)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// A coarse, self-contained heuristic for "this file carries an HEIF transformative property this decoder
    /// doesn't apply" -- see this class's remarks for why irot/imir/clap specifically, and why a byte scan
    /// rather than coupling to <c>AvifItemPropertiesBox</c>.
    /// </summary>
    private static bool HasUnappliedTransformProperty(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> irot = "irot"u8;
        ReadOnlySpan<byte> imir = "imir"u8;
        ReadOnlySpan<byte> clap = "clap"u8;
        return bytes.IndexOf(irot) >= 0 || bytes.IndexOf(imir) >= 0 || bytes.IndexOf(clap) >= 0;
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
    /// See <c>TiffFfmpegReferenceBaseline</c>'s identical remark -- the same hang and blocking-<c>ReadToEnd</c>
    /// risks apply here verbatim, since this is the same process-invocation shape against an equally adversarial
    /// real-world corpus.
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
            // ffprobe doesn't recognize -nostdin the way ffmpeg does (it errors out immediately trying to parse
            // a value for it) -- confirmed empirically, not assumed. ffprobe has no interactive overwrite-style
            // prompt to guard against in the first place, so it doesn't need this.
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
    public static Dictionary<string, AvifFfmpegReferenceRecord> Load()
    {
        var records = new Dictionary<string, AvifFfmpegReferenceRecord>(StringComparer.Ordinal);
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
                records[fields[0]] = new AvifFfmpegReferenceRecord(fields[1], fields[2]);
            }
        }

        return records;
    }

    /// <summary>Rewrites the baseline from freshly computed records.</summary>
    public static void Save(SortedDictionary<string, AvifFfmpegReferenceRecord> records)
    {
        using var writer = new StreamWriter(BaselinePath);
        writer.NewLine = "\n";
        writer.WriteLine("# AVIF/ffmpeg reference baseline: key<TAB>inputHash<TAB>WxH:hexSamples (or SKIPPED).");
        writer.WriteLine($"# hexSamples is up to {SampleCount} evenly-spaced RGBA8 pixels (see AvifFfmpegReferenceBaseline's remarks for why a sample, not a whole-buffer hash) from ffmpeg's independent AVIF decoder (libdav1d). Regenerate with {WriteModeVariable}=write dotnet test --filter AvifFfmpegReferenceTests (requires ffmpeg/ffprobe with libdav1d on PATH), then review the diff.");

        foreach (var (key, record) in records)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key}\t{record.InputHash}\t{record.Result}"));
        }
    }
}
