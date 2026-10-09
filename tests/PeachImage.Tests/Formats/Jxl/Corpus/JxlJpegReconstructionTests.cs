using System.Security.Cryptography;
using System.Text.Json;
using PeachImage.Formats.Jxl;
using PeachImage.Tests.Formats.Jxl.Unit;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Jxl.Corpus;

/// <summary>
/// JPEG XL files made by recompressing a JPEG must give back that JPEG, byte for byte. The libjxl conformance suite
/// publishes the SHA-256 of each expected <c>reconstructed.jpg</c> in its <c>test.json</c>; the libjxl test data pairs
/// <c>1x1_exif_xmp.jxl</c> with its original.
/// </summary>
[Trait("Category", "Corpus")]
public class JxlJpegReconstructionTests
{
    public static IEnumerable<TheoryDataRow<string>> ReconstructionCases()
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(CorpusPaths.ConformanceRoot))
        {
            yield return CorpusSkip.Row("External JPEG XL test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
            yield break;
        }

        foreach (string directory in Directory.EnumerateDirectories(CorpusPaths.ConformanceRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            string testJson = Path.Combine(directory, "test.json");
            string input = Path.Combine(directory, "input.jxl");
            if (File.Exists(testJson) && File.Exists(input) && new FileInfo(input).Length > 200 && ExpectedHash(testJson) is not null)
            {
                yield return new TheoryDataRow<string>(directory);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ReconstructionCases))]
    public void ConformanceCase_ReconstructsTheExactJpeg(string directory)
    {
        string expected = ExpectedHash(Path.Combine(directory, "test.json"))!;
        using var stream = File.OpenRead(Path.Combine(directory, "input.jxl"));

        byte[] jpeg = JxlJpegReconstruction.ReconstructJpeg(stream);

        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant());
    }

    [Fact]
    public void TestDataFile_WithExifAndXmp_ReconstructsTheExactJpeg()
    {
        string directory = Path.Combine(CorpusPaths.TestDataRoot, "jpeg_reconstruction");
        string jxl = Path.Combine(directory, "1x1_exif_xmp.jxl");
        string original = Path.Combine(directory, "1x1_exif_xmp.jpg");
        Assert.SkipUnless(CorpusFixture.IsAvailable && File.Exists(jxl) && File.Exists(original), "The libjxl test data is not available.");

        using var stream = File.OpenRead(jxl);
        Assert.Equal(File.ReadAllBytes(original), JxlJpegReconstruction.ReconstructJpeg(stream));
    }

    [Theory]
    [InlineData("cafe")]
    [InlineData("grayscale_jpeg")]
    public void ReconstructedJpeg_DecodesToTheSamePicture(string name)
    {
        string path = Path.Combine(CorpusPaths.ConformanceRoot, name, "input.jxl");
        Assert.SkipUnless(CorpusFixture.IsAvailable && File.Exists(path), "The libjxl conformance corpus is not available.");

        using var fromJxl = Image.Load(path);
        byte[] jpegBytes;
        using (var stream = File.OpenRead(path))
        {
            jpegBytes = JxlJpegReconstruction.ReconstructJpeg(stream);
        }

        using var fromJpeg = Image.Load(new MemoryStream(jpegBytes), new DecoderOptions { TargetPixelFormat = fromJxl.PixelFormat });
        Assert.Equal(fromJxl.Width, fromJpeg.Width);
        Assert.Equal(fromJxl.Height, fromJpeg.Height);
        Assert.Equal("JPEG", Image.Identify(new MemoryStream(jpegBytes)).FormatName, ignoreCase: true);

        // Two different decoders of the same coefficients: allow rounding and upsampling differences.
        var a = fromJxl.GetPixelSpan();
        var b = fromJpeg.GetPixelSpan();
        long total = 0;
        for (int i = 0; i < a.Length; i++)
        {
            total += Math.Abs(a[i] - b[i]);
        }

        Assert.True((double)total / a.Length < 3.0, $"mean absolute difference {(double)total / a.Length:F2}");
    }

    [Fact]
    public void PlainJxlFile_HasNoReconstructionData()
    {
        byte[] file = JxlTestAssets.Load("rgb_lossless.jxl");

        Assert.False(JxlJpegReconstruction.HasJpegReconstructionData(new MemoryStream(file)));
        Assert.False(JxlJpegReconstruction.TryReconstructJpeg(new MemoryStream(file), out var jpeg));
        Assert.Null(jpeg);
        Assert.Throws<JxlUnsupportedFeatureException>(() => JxlJpegReconstruction.ReconstructJpeg(new MemoryStream(file)));
    }

    [Fact]
    public void NotAJxlFile_IsReportedWithoutReconstructionData()
    {
        var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

        Assert.False(JxlJpegReconstruction.HasJpegReconstructionData(stream));
        stream.Position = 0;
        Assert.False(JxlJpegReconstruction.TryReconstructJpeg(stream, out _));
    }

    [Fact]
    public void StreamOverload_WritesTheSameBytes()
    {
        string path = Path.Combine(CorpusPaths.ConformanceRoot, "grayscale_jpeg", "input.jxl");
        Assert.SkipUnless(CorpusFixture.IsAvailable && File.Exists(path), "The libjxl conformance corpus is not available.");

        using var source = File.OpenRead(path);
        using var destination = new MemoryStream();
        JxlJpegReconstruction.ReconstructJpeg(source, destination);

        using var again = File.OpenRead(path);
        Assert.Equal(JxlJpegReconstruction.ReconstructJpeg(again), destination.ToArray());
    }

    [Fact]
    public void DamagedReconstructionData_FailsWithAJxlException()
    {
        string path = Path.Combine(CorpusPaths.ConformanceRoot, "cafe", "input.jxl");
        Assert.SkipUnless(CorpusFixture.IsAvailable && File.Exists(path), "The libjxl conformance corpus is not available.");

        byte[] file = File.ReadAllBytes(path);
        int jbrd = IndexOf(file, "jbrd"u8);
        Assert.True(jbrd > 0);

        // Deterministic damage inside the jbrd box and inside the codestream: only JPEG XL exceptions are acceptable.
        var random = new Random(12);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            byte[] damaged = (byte[])file.Clone();
            int at = attempt < 20 ? jbrd + 4 + random.Next(0, 200) : random.Next(200, damaged.Length);
            damaged[at] ^= (byte)(1 << random.Next(8));
            try
            {
                JxlJpegReconstruction.ReconstructJpeg(new MemoryStream(damaged));
            }
            catch (JxlFormatException)
            {
            }
        }
    }

    private static string? ExpectedHash(string testJson)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(testJson));
        if (document.RootElement.TryGetProperty("sha256sums", out var sums) && sums.TryGetProperty("reconstructed.jpg", out var hash))
        {
            return hash.GetString();
        }

        return null;
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().IndexOf(pattern);
}
