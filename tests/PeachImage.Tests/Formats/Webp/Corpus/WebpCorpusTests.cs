namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// The official GitHub mirror of Chromium's libwebp-test-data suite: lossless (lossless1-4.webp,
/// color-transform, big-random-alpha, 32 lossless_vec* files), lossy+alpha (lossy_alpha1-4.webp), small
/// edge cases (small_1x1.webp through small_31x13.webp), and known regression fixtures (bug3.webp,
/// bad_palette_index.webp). Exercises real-world-encoded files (not just PeachImage's own decoder-adjacent
/// hand-built fixtures) across both VP8 and VP8L bitstreams and the ALPH chunk.
/// </summary>
public class WebpCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.WebpFiles), MemberType = typeof(CorpusFileSource))]
    public void Files_DecodeGracefullyAndMatchSkiaWhenBothSucceed(string path) =>
        CorpusAssertions.AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(path);
}
