using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using PeachImage.Formats.Webp;

namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>One reference record: what <c>ffmpeg</c>'s independent WebP decoder produced for a given input file, reduced to a hash.</summary>
/// <param name="InputHash">Hash of the raw input file's bytes, so a changed *fixture* is distinguishable from a changed reference.</param>
/// <param name="Result">Either <see cref="WebpFfmpegReferenceBaseline.SkippedMarker"/> (ffmpeg itself couldn't decode this file, the file isn't VP8L -- see this class's remarks for why lossy files are out of scope -- or a dimensions mismatch made the raw output untrustworthy) or <c>WxH:pixelHash</c>.</param>
internal sealed record WebpFfmpegReferenceRecord(string InputHash, string Result);

/// <summary>
/// Reads, writes, and computes the WebP/<c>ffmpeg</c> reference baseline — a flat record of what <c>ffmpeg</c>'s
/// own WebP decoder (backed by libwebp itself, via this build's <c>--enable-libwebp</c>) produces for every
/// single-frame corpus file it can decode. Mirrors <c>TiffFfmpegReferenceBaseline</c>'s shape (baseline TSV,
/// write-mode env var, same hang/partial-decode-safe <c>ffmpeg</c>/<c>ffprobe</c> invocation).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is purely additive, supplementary cross-check -- not a replacement for anything.</b>
/// <see cref="WebpCorpusTests"/> already diffs every corpus file against SkiaSharp (also backed by libwebp)
/// with a documented tolerance, and <see cref="WebpDecodeHashTests"/> already pins this decoder's own prior
/// output as a tight regression detector. This class exists only because both of those go through the *same*
/// underlying reference library (libwebp) as their oracle, so an <c>ffmpeg</c>-based check is genuinely
/// independent packaging of that same reference decoder, not a different one -- still worth having as a
/// second, differently-built consumer of it.
/// </para>
/// <para>
/// <b>Why VP8L (lossless) only -- confirmed empirically, not assumed.</b> Decoding the same file both via
/// <c>-pix_fmt rgba</c> here and by inspecting PeachImage's own decode: for a VP8L (lossless) file
/// (<c>lossless1.webp</c>), every sampled pixel -- including a mid-image, non-edge region -- matched
/// <c>ffmpeg</c>'s raw output byte-for-byte. For a VP8 (lossy, with alpha) file
/// (<c>alpha_filter_0_method_0.webp</c>), most pixels were off by 1 in one or more RGB channels (alpha always
/// matched exactly). VP8L decodes directly to ARGB with no color-space conversion, so
/// <c>-pix_fmt rgba</c> is a pure channel-order relabel with no precision-changing math on either side -- the
/// same reasoning <c>TiffFfmpegReferenceBaseline</c> uses for its own 8-bit-RGBA-only scope. VP8 (lossy) is
/// fundamentally different: it's YUV 4:2:0, so <c>-pix_fmt rgba</c> routes through <c>libswscale</c>'s own
/// YUV-&gt;RGB conversion and chroma upsampling, which is independent of (and, confirmed here, rounds
/// slightly differently than) libwebp's own <c>WebPDecodeRGBA</c>-style conversion that both this decoder and
/// SkiaSharp's differential in <see cref="WebpCorpusTests"/> are calibrated against (that class's own
/// <c>MaxChannelTolerance</c> remark documents an analogous, already-accepted ~1-6 rounding gap from exactly
/// this kind of implementation divergence). Comparing lossy files against <c>ffmpeg</c>'s raw output would
/// therefore be comparing against a third, <c>libswscale</c>-flavored conversion of the same decoded samples,
/// not really "libwebp's answer" at all -- not a meaningful independent check, and not worth chasing with a
/// second tolerance constant when <see cref="WebpCorpusTests"/> already covers lossy correctness (against the
/// real libwebp-backed oracle, not a swscale-filtered one) with sample coverage this class can't match anyway
/// (SkiaSharp runs in-process, so it can afford a full, untrimmed per-pixel diff every test run; this class
/// spawns an external process per file and freezes the result into a baseline instead). Detected via a plain
/// RIFF chunk scan for a top-level <c>VP8L</c> fourCC (lossless) vs. <c>VP8 </c> (lossy) -- self-contained
/// rather than coupling this test project to <c>WebpContainerReader</c>'s internals.
/// </para>
/// </remarks>
internal static class WebpFfmpegReferenceBaseline
{
    /// <summary>Recorded in place of a pixel hash when <c>ffmpeg</c> itself can't decode this file, the file isn't VP8L (see this class's remarks), it's animated (out of scope -- <see cref="WebpAnimatedCorpusTests"/> already covers real-world animated WebP), or a dimensions mismatch made the raw output untrustworthy.</summary>
    public const string SkippedMarker = "SKIPPED";

    /// <summary>Set to <c>write</c> to regenerate <see cref="BaselinePath"/> instead of asserting against it. Requires <c>ffmpeg</c>/<c>ffprobe</c> on PATH — normal (non-write-mode) test runs never invoke either.</summary>
    public const string WriteModeVariable = "PEACHIMAGE_WEBP_FFMPEG_BASELINE";

    public static string BaselinePath { get; } = Path.Combine(
        RepoRoot, "tests", "PeachImage.Tests", "Formats", "Webp", "Corpus", "WebpFfmpegReference.baseline.tsv");

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

    /// <summary>
    /// Every input the baseline covers: the libwebp-test-data corpus (the same single-frame source
    /// <see cref="CorpusFileSource.WebpFiles"/>/<see cref="WebpCorpusTests"/> use) -- deliberately not the
    /// Skia animated fixtures <see cref="CorpusFileSource.AnimatedWebpFiles"/>/<see cref="WebpAnimatedCorpusTests"/>
    /// cover (a disjoint directory, so there's no overlap to filter here even in principle; <see cref="Compute"/>
    /// still independently skips anything <see cref="WebpDecoder.Identify"/> reports as animated, as a
    /// defense-in-depth match for that scoping rather than a load-bearing one).
    /// </summary>
    public static SortedDictionary<string, string> EnumerateInputs()
    {
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (Directory.Exists(CorpusPaths.LibwebpTestDataRoot))
        {
            foreach (var file in Directory.EnumerateFiles(CorpusPaths.LibwebpTestDataRoot, "*.webp", SearchOption.AllDirectories))
            {
                inputs["corpus/" + Path.GetRelativePath(CorpusPaths.LibwebpTestDataRoot, file).Replace('\\', '/')] = file;
            }
        }

        return inputs;
    }

    /// <summary>
    /// Files confirmed, individually, to carry deliberately-malformed data for which independent decoders'
    /// behavior legitimately differs -- not a meaningful correctness signal, mirroring
    /// <c>TiffFfmpegReferenceBaseline</c>'s exclusion of its corpus's whole "robustness" bucket for the same
    /// reason (that corpus has no such bucket to exclude wholesale, so this is a per-file list instead).
    /// <c>bad_palette_index.webp</c> (VP8L, deliberately invalid color-indexing/palette index data per its own
    /// name): investigated by diffing raw pixel bytes -- every RGB value <c>ffmpeg</c> and this decoder agree
    /// on matches exactly, and <see cref="WebpCorpusTests"/>'s SkiaSharp/libwebp differential already passes
    /// for this file too, corroborating this decoder's reconstruction. The only disagreement is whether the
    /// image has an alpha channel at all (this decoder reads VP8L's own <c>alpha_is_used</c> header bit as
    /// false and returns opaque Rgb24; <c>ffmpeg</c>'s raw output contains scattered fully-transparent pixels)
    /// -- exactly the kind of undefined-for-malformed-input divergence between two independent decoders'
    /// fallback behavior that isn't a bug in either.
    /// </summary>
    private static readonly HashSet<string> KnownMalformedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "bad_palette_index.webp",
    };

    /// <summary>Runs <c>ffmpeg</c>/<c>ffprobe</c> against <paramref name="path"/> and reduces the result to a baseline record. Never throws.</summary>
    public static WebpFfmpegReferenceRecord Compute(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string inputHash = Convert.ToHexString(XxHash128.Hash(bytes));

        if (!IsLosslessBitstream(bytes) || IsAnimated(path) || KnownMalformedFiles.Contains(Path.GetFileName(path)))
        {
            return new WebpFfmpegReferenceRecord(inputHash, SkippedMarker);
        }

        string tempFile = Path.Combine(Path.GetTempPath(), $"peachimage-webp-ffmpeg-{Guid.NewGuid():N}.raw");
        try
        {
            bool ran = TryRunProcess("ffmpeg", ["-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgba", "-y", tempFile], out _, out string ffmpegStderr);

            // At `-v error` verbosity a genuinely clean decode prints nothing to stderr at all, so *any*
            // stderr output -- not just a nonzero exit code -- means this file's "reference" isn't
            // trustworthy ground truth (mirrors TiffFfmpegReferenceBaseline's quad-lzw.tif finding: ffmpeg
            // can exit 0 while only partially decoding a file).
            if (!ran || ffmpegStderr.Length > 0 || !File.Exists(tempFile))
            {
                return new WebpFfmpegReferenceRecord(inputHash, SkippedMarker);
            }

            byte[] raw = File.ReadAllBytes(tempFile);
            var (width, height) = ProbeDimensions(path);

            if (width <= 0 || height <= 0 || raw.Length == 0 || (long)width * height * 4 != raw.Length)
            {
                return new WebpFfmpegReferenceRecord(inputHash, SkippedMarker);
            }

            string pixelHash = Convert.ToHexString(XxHash128.Hash(raw));
            return new WebpFfmpegReferenceRecord(inputHash, $"{width}x{height}:{pixelHash}");
        }
        catch
        {
            return new WebpFfmpegReferenceRecord(inputHash, SkippedMarker);
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

    /// <summary>
    /// A plain RIFF chunk walk for a top-level <c>VP8L</c> fourCC -- see this class's remarks for why only a
    /// lossless bitstream is safe to compare byte-for-byte. Self-contained (no dependency on
    /// <c>WebpContainerReader</c>'s internals) at the cost of being a simple structural scan rather than a full
    /// parse; a malformed/truncated chunk here just falls through to "not lossless" (SKIPPED), never a false
    /// "is lossless".
    /// </summary>
    private static bool IsLosslessBitstream(byte[] bytes)
    {
        if (bytes.Length < 12 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F'
            || bytes[8] != 'W' || bytes[9] != 'E' || bytes[10] != 'B' || bytes[11] != 'P')
        {
            return false;
        }

        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            char c0 = (char)bytes[offset];
            char c1 = (char)bytes[offset + 1];
            char c2 = (char)bytes[offset + 2];
            char c3 = (char)bytes[offset + 3];
            uint size = BitConverter.ToUInt32(bytes, offset + 4);

            if (c0 == 'V' && c1 == 'P' && c2 == '8' && c3 == 'L')
            {
                return true;
            }

            if (c0 == 'V' && c1 == 'P' && c2 == '8' && c3 == ' ')
            {
                return false;
            }

            // Chunks are padded to an even size; a chunk claiming more data than remains in the file means
            // this file is truncated/malformed -- stop rather than reading out of bounds.
            long next = (long)offset + 8 + size + (size % 2);
            if (size > int.MaxValue - 8 || next > bytes.Length)
            {
                return false;
            }

            offset = (int)next;
        }

        return false;
    }

    private static bool IsAnimated(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return WebpDecoder.Identify(stream).IsAnimated;
        }
        catch (WebpDecodingException)
        {
            return false;
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
    /// See <c>TiffFfmpegReferenceBaseline</c>'s identical remark -- the same hang and blocking-<c>ReadToEnd</c>
    /// risks apply here verbatim, since this is the same process-invocation shape against an equally adversarial
    /// real-world corpus (this one includes deliberate regression fixtures like <c>bug3.webp</c>/
    /// <c>bad_palette_index.webp</c>).
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
    public static Dictionary<string, WebpFfmpegReferenceRecord> Load()
    {
        var records = new Dictionary<string, WebpFfmpegReferenceRecord>(StringComparer.Ordinal);
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
                records[fields[0]] = new WebpFfmpegReferenceRecord(fields[1], fields[2]);
            }
        }

        return records;
    }

    /// <summary>Rewrites the baseline from freshly computed records.</summary>
    public static void Save(SortedDictionary<string, WebpFfmpegReferenceRecord> records)
    {
        using var writer = new StreamWriter(BaselinePath);
        writer.NewLine = "\n";
        writer.WriteLine("# WebP/ffmpeg reference baseline: key<TAB>inputHash<TAB>WxH:pixelHash (or SKIPPED).");
        writer.WriteLine($"# Generated from ffmpeg's WebP decoder (libwebp-backed), 8-bit RGBA, VP8L (lossless) files only -- see WebpFfmpegReferenceBaseline's remarks for why lossy is out of scope. Regenerate with {WriteModeVariable}=write dotnet test --filter WebpFfmpegReferenceTests (requires ffmpeg/ffprobe on PATH), then review the diff.");

        foreach (var (key, record) in records)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key}\t{record.InputHash}\t{record.Result}"));
        }
    }
}
