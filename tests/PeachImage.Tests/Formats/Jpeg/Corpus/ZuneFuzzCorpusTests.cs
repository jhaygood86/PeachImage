namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// The zune-image fuzz corpus: ~1,800 minimal JPEG inputs designed to exercise edge cases (CMYK,
/// progressive, unusual subsampling) and crash/hang/memory-safety bugs rather than being meaningful images —
/// only graceful accept-or-reject matters here, not pixel fidelity.
/// </summary>
public class ZuneFuzzCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.ZuneFuzzFiles), MemberType = typeof(CorpusFileSource))]
    public void DecodesWithoutCrashingOrHanging(string path) => CorpusAssertions.AssertDecodesGracefully(path);
}
