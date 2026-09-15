using System.Collections.Concurrent;

namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// The zune-image fuzz corpus: ~1,800 minimal JPEG inputs designed to exercise edge cases (CMYK,
/// progressive, unusual subsampling) and crash/hang/memory-safety bugs rather than being meaningful images —
/// only graceful accept-or-reject matters here, not pixel fidelity.
/// </summary>
[Trait("Category", "Corpus")]
public class ZuneFuzzCorpusTests
{
    /// <summary>
    /// One aggregate <c>[Fact]</c> parallelized over every fuzz file, rather than one xUnit <c>[Theory]</c>
    /// case per file: ~1,800 individual cases all land in this class's single collection (xUnit parallelizes
    /// across collections, not within one), so they'd run serially on one thread. Each file's decode attempt is
    /// independent, so <see cref="Parallel.ForEach{TSource}(IEnumerable{TSource},Action{TSource})"/> lets this
    /// actually use more than one core while still reporting exactly which file(s) failed.
    /// </summary>
    [Fact]
    public void DecodesWithoutCrashingOrHanging()
    {
        if (!CorpusFixture.IsAvailable)
        {
            return;
        }

        var failures = new ConcurrentBag<string>();

        Parallel.ForEach(CorpusFileSource.ZuneFuzzFilePaths(), path =>
        {
            try
            {
                CorpusAssertions.AssertDecodesGracefully(path);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        });

        Assert.True(failures.IsEmpty, $"{failures.Count} file(s) failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
