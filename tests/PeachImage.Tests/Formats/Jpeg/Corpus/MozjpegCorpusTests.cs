namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// The mozjpeg reference sample set: progressive JPEGs, ICC-profile-carrying files, and a 12-bit sample
/// (out of scope — expected to be rejected with a JpegFormatException, not decoded).
/// </summary>
public class MozjpegCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.MozjpegFiles), MemberType = typeof(CorpusFileSource))]
    public void DecodesGracefullyAndMatchesSkiaWhenBothSucceed(string path) =>
        CorpusAssertions.AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(path);
}
