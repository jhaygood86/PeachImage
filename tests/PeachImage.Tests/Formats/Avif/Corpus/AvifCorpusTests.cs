namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// Runs the real <c>AOMediaCodec/libavif</c> <c>tests/data</c> corpus (basic stills, high-bit-depth,
/// HDR/wide-gamut, grid-tiled, gain maps, animation, metadata edge cases -- a much broader mix of
/// real-world and adversarial AVIF files than the hand-built fixtures in <c>Unit/</c>) through both
/// container parsing (<see cref="Files_IdentifyGracefully"/>) and full pixel decode
/// (<see cref="Files_DecodeGracefully"/>). The latter is scoped to whatever this phase's AV1 decoder
/// actually supports (single non-grid 8-bit items, no alpha, no in-loop filters yet) -- most real corpus
/// files legitimately throw <see cref="AvifUnsupportedFeatureException"/>, which is a scope fact, not a
/// failure; see the project plan for where the ffmpeg-differential pixel comparison lands once in-loop
/// filters and the broader format matrix are implemented.
/// </summary>
public class AvifCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.AvifFiles), MemberType = typeof(CorpusFileSource))]
    public void Files_IdentifyGracefully(string path) => AvifCorpusAssertions.AssertIdentifiesGracefully(path);

    [Theory]
    [MemberData(nameof(CorpusFileSource.AvifFiles), MemberType = typeof(CorpusFileSource))]
    public void Files_DecodeGracefully(string path) => AvifCorpusAssertions.AssertDecodesGracefully(path);
}
