namespace PeachImage.Tests.Formats.Bmp.Corpus;

/// <summary>
/// The Imazen <c>codec-corpus</c> BMP conformance set — 126 files generated from Jason Summers' bmpsuite
/// (plus a few Pillow/zune-image additions and two real photos), spanning every header variant, bit depth,
/// compression mode, and bitfield mask combination. Split into <c>valid</c>, <c>non-conformant</c>, and
/// <c>invalid</c> subsets.
/// </summary>
public class BmpConformanceCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.ValidFiles), MemberType = typeof(CorpusFileSource))]
    public void ValidFiles_DecodeGracefullyAndMatchSkiaWhenBothSucceed(string path) =>
        CorpusAssertions.AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(path);

    /// <summary>
    /// As with Jpeg, "non-conformant" means real-world decoders may legitimately disagree — confirmed
    /// empirically here too: files probing reversed/unusual bitfield mask byte orders and cut-off/edge-case
    /// RLE streams produce genuinely different (not merely off-by-rounding) output between PeachImage and
    /// SkiaSharp. Only graceful accept-or-reject is required for this bucket, never pixel fidelity.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusFileSource.NonConformantFiles), MemberType = typeof(CorpusFileSource))]
    public void NonConformantFiles_DecodeGracefully(string path) =>
        CorpusAssertions.AssertDecodesGracefully(path);

    [Theory]
    [MemberData(nameof(CorpusFileSource.InvalidFiles), MemberType = typeof(CorpusFileSource))]
    public void InvalidFiles_DecodeGracefully(string path) =>
        CorpusAssertions.AssertDecodesGracefully(path);
}
