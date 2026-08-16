using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using PeachImage.Formats.Webp;

namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>One baseline record: what decoding a given input file produced, reduced to a hash.</summary>
/// <param name="InputHash">Hash of the raw input file's bytes, so a changed *fixture* is distinguishable from a changed *decoder*.</param>
/// <param name="Result">Either <see cref="WebpDecodeHashBaseline.RejectedMarker"/> or <c>WxH:PixelFormat:outputHash</c>.</param>
internal sealed record WebpDecodeHashRecord(string InputHash, string Result);

/// <summary>
/// Reads, writes, and computes the WebP decode hash baseline — a flat record of exactly what pixels every
/// corpus and benchmark asset currently decodes to. It answers "did the decoder's output change", which is the
/// question that matters when refactoring for speed; <see cref="CorpusAssertions"/> answers the different
/// question "is the decoder correct", by diffing against SkiaSharp.
/// </summary>
/// <remarks>
/// The input hash is stored alongside the output hash deliberately. The corpus is gitignored and fetched from
/// the network, so without it a mismatch is ambiguous between "upstream libwebp-test-data changed this fixture"
/// and "we broke the decoder" — a genuinely painful thing to diagnose after the fact.
/// </remarks>
internal static class WebpDecodeHashBaseline
{
    /// <summary>Recorded in place of a pixel hash for an input PeachImage deliberately refuses (an animated WebP, or a malformed file).</summary>
    public const string RejectedMarker = "REJECTED";

    /// <summary>Set to <c>write</c> to regenerate <see cref="BaselinePath"/> instead of asserting against it.</summary>
    public const string WriteModeVariable = "PEACHIMAGE_WEBP_HASH_BASELINE";

    /// <summary>The checked-in baseline, resolved against the repo root so write mode updates the source tree rather than a build output copy.</summary>
    public static string BaselinePath { get; } = Path.Combine(
        RepoRoot, "tests", "PeachImage.Tests", "Formats", "Webp", "Corpus", "WebpDecodeHashes.baseline.tsv");

    /// <summary>Whether the current run should rewrite the baseline rather than assert against it.</summary>
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
    /// Every input the baseline covers, as stable <c>key -&gt; absolute path</c> pairs. Keys are corpus-relative
    /// so the baseline is machine-independent. The six benchmark assets are included alongside the 131 corpus
    /// files because they're the only 1080p inputs available — the only ones large enough to exercise
    /// multi-partition coefficient decode at scale, or to leave room for a "wrong only in the last vector lane"
    /// bug to show up at all.
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

        string benchAssets = Path.Combine(RepoRoot, "bench", "PeachImage.Benchmarks", "Assets");
        if (Directory.Exists(benchAssets))
        {
            foreach (var file in Directory.EnumerateFiles(benchAssets, "*.webp"))
            {
                inputs["bench/" + Path.GetFileName(file)] = file;
            }
        }

        return inputs;
    }

    /// <summary>Decodes <paramref name="path"/> and reduces the result to a baseline record.</summary>
    public static WebpDecodeHashRecord Compute(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string inputHash = Convert.ToHexString(XxHash128.Hash(bytes));

        try
        {
            using var stream = new MemoryStream(bytes);
            using var image = WebpDecoder.Decode(stream);
            return new WebpDecodeHashRecord(inputHash, $"{image.Width}x{image.Height}:{image.PixelFormat}:{HashPixels(image)}");
        }
        catch (WebpDecodingException)
        {
            return new WebpDecodeHashRecord(inputHash, RejectedMarker);
        }
    }

    /// <summary>Hashes the decoded dimensions, pixel format, and every pixel byte — so a change in any of them is a mismatch.</summary>
    private static string HashPixels(Image image)
    {
        var hash = new XxHash128();

        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(header[..4], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], image.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], (int)image.PixelFormat);
        hash.Append(header);
        hash.Append(image.GetPixelSpan());

        return Convert.ToHexString(hash.GetCurrentHash());
    }

    /// <summary>Loads the checked-in baseline, or an empty map when it doesn't exist yet.</summary>
    public static Dictionary<string, WebpDecodeHashRecord> Load()
    {
        var records = new Dictionary<string, WebpDecodeHashRecord>(StringComparer.Ordinal);
        if (!File.Exists(BaselinePath))
        {
            return records;
        }

        foreach (var line in File.ReadLines(BaselinePath))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] fields = line.Split('\t');
            if (fields.Length == 3)
            {
                records[fields[0]] = new WebpDecodeHashRecord(fields[1], fields[2]);
            }
        }

        return records;
    }

    /// <summary>Rewrites the baseline from freshly computed records.</summary>
    public static void Save(SortedDictionary<string, WebpDecodeHashRecord> records)
    {
        using var writer = new StreamWriter(BaselinePath);
        writer.NewLine = "\n";
        writer.WriteLine("# WebP decode output baseline: key<TAB>inputHash<TAB>WxH:PixelFormat:pixelHash (or REJECTED).");
        writer.WriteLine($"# Regenerate with {WriteModeVariable}=write dotnet test --filter WebpDecodeHashTests, then review the diff.");

        foreach (var (key, record) in records)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key}\t{record.InputHash}\t{record.Result}"));
        }
    }
}
