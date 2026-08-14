namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// The Imazen <c>codec-corpus</c> JPEG conformance set: valid, invalid, non-conformant, and crash-reproducer
/// files spanning 12 camera manufacturers, CMYK/YCCK color spaces, and various sampling configurations.
/// </summary>
public class ImazenConformanceCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.ImazenConformanceValidFiles), MemberType = typeof(CorpusFileSource))]
    public void ValidFiles_DecodeGracefullyAndMatchSkiaWhenBothSucceed(string path) =>
        CorpusAssertions.AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(path);

    /// <summary>
    /// Invalid/non-conformant/crash-repro files are, by construction, files where real-world decoders are
    /// known to disagree (each crash-repro subfolder is literally named after the decoder/issue it
    /// reproduces). Only graceful accept-or-reject is required here — never pixel agreement with SkiaSharp.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusFileSource.ImazenConformanceNonConformantFiles), MemberType = typeof(CorpusFileSource))]
    public void NonConformantFiles_DecodeWithoutCrashingOrHanging(string path) =>
        CorpusAssertions.AssertDecodesGracefully(path);
}
