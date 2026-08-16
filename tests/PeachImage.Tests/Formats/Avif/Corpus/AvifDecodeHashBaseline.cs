using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>One baseline record: what decoding a given input file produced, reduced to a hash.</summary>
/// <param name="InputHash">Hash of the raw input file's bytes, so a changed *fixture* is distinguishable from a changed *decoder*.</param>
/// <param name="Result">One of <see cref="AvifDecodeHashBaseline.RejectedMarker"/>/<see cref="AvifDecodeHashBaseline.SkippedMarker"/>, or <c>WxH:PixelFormat:outputHash</c>.</param>
internal sealed record AvifDecodeHashRecord(string InputHash, string Result);

/// <summary>
/// Reads, writes, and computes the AVIF decode hash baseline — a flat record of exactly what pixels every
/// corpus asset currently decodes to. It answers "did the decoder's output change", the question that
/// matters when refactoring for speed (vectorizing a kernel, pooling a buffer); <c>AvifCorpusTests</c>
/// answers the different question "does decode behave gracefully" (succeed, or throw a recognized
/// exception, never hang or crash) without asserting exact pixel values. Mirrors
/// <c>WebpDecodeHashBaseline</c>'s design, including its "the input hash guards against a silently-changed
/// upstream fixture" rationale — the corpus here is equally gitignored and network-fetched.
/// </summary>
internal static class AvifDecodeHashBaseline
{
    /// <summary>Recorded in place of a pixel hash for an input this phase's decoder can't yet handle (grid/alpha/bit-depth/feature scope, or a genuinely malformed file) -- distinct from <see cref="RejectedMarker"/> so a shrinking "skipped" count is visible as real progress.</summary>
    public const string SkippedMarker = "SKIPPED";

    /// <summary>Recorded in place of a pixel hash for an input that's a genuinely malformed AVIF bitstream (an <c>AvifDecodingException</c>, not an <c>AvifUnsupportedFeatureException</c>).</summary>
    public const string RejectedMarker = "REJECTED";

    /// <summary>Set to <c>write</c> to regenerate <see cref="BaselinePath"/> instead of asserting against it.</summary>
    public const string WriteModeVariable = "PEACHIMAGE_AVIF_HASH_BASELINE";

    public static string BaselinePath { get; } = Path.Combine(
        RepoRoot, "tests", "PeachImage.Tests", "Formats", "Avif", "Corpus", "AvifDecodeHashes.baseline.tsv");

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

    /// <summary>Decodes <paramref name="path"/> and reduces the result to a baseline record.</summary>
    public static AvifDecodeHashRecord Compute(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string inputHash = Convert.ToHexString(XxHash128.Hash(bytes));

        try
        {
            using var stream = new MemoryStream(bytes);
            using var image = AvifDecoder.Decode(stream);
            return new AvifDecodeHashRecord(inputHash, $"{image.Width}x{image.Height}:{image.PixelFormat}:{HashPixels(image)}");
        }
        catch (AvifUnsupportedFeatureException)
        {
            return new AvifDecodeHashRecord(inputHash, SkippedMarker);
        }
        catch (AvifDecodingException)
        {
            return new AvifDecodeHashRecord(inputHash, RejectedMarker);
        }
    }

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

    public static Dictionary<string, AvifDecodeHashRecord> Load()
    {
        var records = new Dictionary<string, AvifDecodeHashRecord>(StringComparer.Ordinal);
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
                records[fields[0]] = new AvifDecodeHashRecord(fields[1], fields[2]);
            }
        }

        return records;
    }

    public static void Save(SortedDictionary<string, AvifDecodeHashRecord> records)
    {
        using var writer = new StreamWriter(BaselinePath);
        writer.NewLine = "\n";
        writer.WriteLine("# AVIF decode output baseline: key<TAB>inputHash<TAB>WxH:PixelFormat:pixelHash (or REJECTED/SKIPPED).");
        writer.WriteLine($"# Regenerate with {WriteModeVariable}=write dotnet test --filter AvifDecodeHashTests, then review the diff.");

        foreach (var (key, record) in records)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key}\t{record.InputHash}\t{record.Result}"));
        }
    }
}
