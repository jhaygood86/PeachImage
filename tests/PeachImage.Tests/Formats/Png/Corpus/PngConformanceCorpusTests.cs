namespace PeachImage.Tests.Formats.Png.Corpus;

/// <summary>
/// The Imazen <c>codec-corpus</c> mirror of Willem van Schaik's classic PngSuite conformance set —
/// 176 files spanning every color type × bit depth combination, Adam7 interlacing, all 5 filter types,
/// odd sizes, zlib compression levels, transparency, and ancillary chunks, plus 14 deliberately
/// corrupt/malformed files.
/// </summary>
public class PngConformanceCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.ValidFiles), MemberType = typeof(CorpusFileSource))]
    public void ValidFiles_DecodeGracefullyAndMatchSkiaWhenBothSucceed(string path) =>
        PngCorpusAssertions.AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(path);

    [Theory]
    [MemberData(nameof(CorpusFileSource.InvalidFiles), MemberType = typeof(CorpusFileSource))]
    public void InvalidFiles_DecodeGracefully(string path) =>
        PngCorpusAssertions.AssertDecodesGracefully(path);
}
