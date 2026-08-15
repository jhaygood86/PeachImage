namespace PeachImage.Tests.Formats.Gif.Corpus;

/// <summary>
/// The W3C image-format test page's GIF assets (w3.org/People/mimasa/test/imgformat/): standard, 256-color,
/// grayscale, and black/white GIFs plus an animated GIF.
/// </summary>
public class W3cCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.W3cFiles), MemberType = typeof(CorpusFileSource))]
    public void Files_DecodeGracefullyAndMatchSkiaWhenBothSucceed(string path) =>
        CorpusAssertions.AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(path);
}
