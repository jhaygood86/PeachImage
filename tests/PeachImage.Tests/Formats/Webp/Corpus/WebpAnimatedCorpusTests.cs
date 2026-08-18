namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// Real-world animated WebP fixtures from Skia's own <c>resources/images</c> test resources (see
/// <see cref="SkiaCorpusFetcher"/>) — libwebp-test-data (<see cref="WebpCorpusTests"/>'s source) has no
/// animated WebP files at all, so this is the only real-world (not hand-built) animated-WebP corpus coverage.
/// </summary>
public class WebpAnimatedCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.AnimatedWebpFiles), MemberType = typeof(CorpusFileSource))]
    public void Files_DecodeGracefullyAndMatchSkiaFrameByFrame(string path) =>
        AnimatedCorpusAssertions.AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(path);
}
