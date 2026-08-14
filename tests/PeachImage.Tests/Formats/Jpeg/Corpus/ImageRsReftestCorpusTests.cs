namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// The image-rs/jpeg-decoder repository's own test assets: crash-reproducer images (many sourced from a
/// broader "imagetestsuite" fuzz corpus), reference images (paired .jpg/.png in <c>tests/reftest/images</c>,
/// including lossless-JPEG and mozilla-sourced samples), and ICC-chunking edge cases.
/// </summary>
public class ImageRsReftestCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.ImageRsCrashtestFiles), MemberType = typeof(CorpusFileSource))]
    public void CrashtestImages_DecodeWithoutCrashingOrHanging(string path) => CorpusAssertions.AssertDecodesGracefully(path);

    [Theory]
    [MemberData(nameof(CorpusFileSource.ImageRsReftestFiles), MemberType = typeof(CorpusFileSource))]
    public void ReftestImages_DecodeGracefullyAndMatchSkiaWhenBothSucceed(string path) =>
        CorpusAssertions.AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(path);

    [Theory]
    [MemberData(nameof(CorpusFileSource.ImageRsIccFiles), MemberType = typeof(CorpusFileSource))]
    public void IccEdgeCases_DecodeWithoutCrashingOrHanging(string path) => CorpusAssertions.AssertDecodesGracefully(path);
}
