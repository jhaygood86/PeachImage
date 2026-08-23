namespace PeachImage.Tests.Formats.Tiff.Corpus;

/// <summary>
/// The Imazen <c>codec-corpus</c> TIFF conformance set — 154 files sourced from libtiff, image-tiff, and
/// image-rs's own test suites, spanning far more of TIFF's feature matrix (9 compression types, 1-64 bit
/// integer/float samples, 8 color models, tiled/planar/predictor variants, BigTIFF) than this decoder's
/// declared baseline scope (uncompressed/LZW/PackBits, 1/2/4/8/16-bit, grayscale/RGB/palette/CMYK). Split
/// into <c>valid</c> (must decode correctly or throw a well-typed exception), <c>edge-cases</c> (legitimate
/// but uncommon — multi-IFD documents, SubIFD chains, GeoTIFF metadata), and <c>robustness</c> (malformed —
/// circular/self-referential IFD chains, a truncated LZW stream) subsets.
/// </summary>
public class TiffConformanceCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.ValidFiles), MemberType = typeof(CorpusFileSource))]
    public void ValidFiles_DecodeGracefully(string path) => CorpusAssertions.AssertDecodesGracefully(path);

    [Theory]
    [MemberData(nameof(CorpusFileSource.EdgeCaseFiles), MemberType = typeof(CorpusFileSource))]
    public void EdgeCaseFiles_DecodeGracefully(string path) => CorpusAssertions.AssertDecodesGracefully(path);

    [Theory]
    [MemberData(nameof(CorpusFileSource.RobustnessFiles), MemberType = typeof(CorpusFileSource))]
    public void RobustnessFiles_DecodeGracefully(string path) => CorpusAssertions.AssertDecodesGracefully(path);
}
