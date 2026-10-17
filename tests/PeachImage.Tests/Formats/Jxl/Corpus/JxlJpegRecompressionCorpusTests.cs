using PeachImage.Formats.Jxl;
using PeachImage.Formats.Jxl.Jpeg;
using PeachImage.Tests.Formats.Jpeg.Corpus;

namespace PeachImage.Tests.Formats.Jxl.Corpus;

/// <summary>Real-world JPEGs from the JPEG corpora: every one the parser accepts has to be written back byte for byte.</summary>
[Trait("Category", "Corpus")]
public class JxlJpegRecompressionCorpusTests
{
    private static IEnumerable<string> JpegFiles()
    {
        if (!Jpeg.Corpus.CorpusFixture.IsAvailable || !Directory.Exists(Jpeg.Corpus.CorpusPaths.Root))
        {
            yield break;
        }

        foreach (string directory in new[] { Jpeg.Corpus.CorpusPaths.ImazenRoot, Jpeg.Corpus.CorpusPaths.ImageRsRoot })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                string extension = Path.GetExtension(file);
                if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    [Fact]
    public void ParsedJpegs_AreWrittenBackByteForByte()
    {
        string[] files = [.. JpegFiles()];
        Assert.SkipWhen(files.Length == 0, "The external JPEG test corpus is not available.");

        int accepted = 0;
        var mismatches = new List<string>();
        foreach (string file in files)
        {
            byte[] jpeg = File.ReadAllBytes(file);
            JxlParsedJpeg parsed;
            try
            {
                parsed = JxlJpegParser.Parse(jpeg);
            }
            catch (JxlUnsupportedFeatureException)
            {
                continue;
            }

            accepted++;
            try
            {
                if (!JxlJpegWriter.Write(parsed.Data).AsSpan().SequenceEqual(jpeg))
                {
                    mismatches.Add(Path.GetFileName(file));
                }
            }
            catch (JxlFormatException)
            {
                mismatches.Add(Path.GetFileName(file) + " (writer failed)");
            }
        }

        Assert.True(accepted > 20, $"Only {accepted} of {files.Length} corpus JPEGs were accepted.");
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {accepted} accepted JPEGs were not written back exactly: {string.Join(", ", mismatches.Take(10))}");
    }

    [Fact]
    public void AcceptedJpegs_RecompressAndComeBackByteForByte()
    {
        string[] files = [.. JpegFiles()];
        Assert.SkipWhen(files.Length == 0, "The external JPEG test corpus is not available.");

        int recompressed = 0;
        var declined = new List<string>();
        foreach (string file in files)
        {
            byte[] jpeg = File.ReadAllBytes(file);
            try
            {
                JxlJpegParser.Parse(jpeg);
            }
            catch (JxlUnsupportedFeatureException)
            {
                continue;
            }

            if (JxlJpegRecompression.TryRecompressJpeg(new MemoryStream(jpeg), out byte[]? jxl))
            {
                recompressed++;
                Assert.Equal(jpeg, JxlJpegReconstruction.ReconstructJpeg(new MemoryStream(jxl)));
            }
            else
            {
                declined.Add(Path.GetFileName(file));
            }
        }

        Assert.True(recompressed > 20, $"Only {recompressed} corpus JPEGs were recompressed.");
        Assert.True(declined.Count == 0, $"{declined.Count} parsed JPEGs could not be recompressed: {string.Join(", ", declined.Take(20))}");
    }
}
