namespace PeachImage.Tests.Formats.Gif.Corpus;

/// <summary>
/// giflib's own regression images (<c>pic/</c> plus <c>tests/wedge.gif</c>): fire.gif, gifgrid.gif,
/// porsche.gif, solid2.gif, treescap.gif (and its interlaced counterpart), welcome2.gif, x-trans.gif
/// (transparency), wedge.gif. Specifically exercises interlacing, transparency, and general LZW correctness
/// against real-world-encoded files (not just PeachImage's own encoder output).
/// </summary>
public class GiflibCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.GiflibFiles), MemberType = typeof(CorpusFileSource))]
    public void Files_DecodeGracefullyAndMatchSkiaWhenBothSucceed(string path) =>
        CorpusAssertions.AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(path);
}
