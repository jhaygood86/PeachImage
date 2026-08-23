namespace PeachImage.Tests.Formats.Tiff.Corpus;

/// <summary>
/// Makes corpus availability an explicit, visible signal rather than a silent "0 tests ran" — if the corpus
/// couldn't be fetched (no network, opted out via PEACHIMAGE_SKIP_CORPUS_FETCH), this reports as Skipped so
/// it's obvious from the test run output, instead of corpus-driven tests just quietly vanishing.
/// </summary>
public class CorpusAvailabilityTests
{
    [Fact]
    public void Corpus_IsAvailable()
    {
        if (!CorpusFixture.IsAvailable)
        {
            Assert.Skip("External TIFF test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set). Corpus-driven tests will report zero cases.");
        }
    }
}
