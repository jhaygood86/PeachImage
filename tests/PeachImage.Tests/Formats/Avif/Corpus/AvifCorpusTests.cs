namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// Runs the real <c>AOMediaCodec/libavif</c> <c>tests/data</c> corpus (basic stills, high-bit-depth,
/// HDR/wide-gamut, grid-tiled, gain maps, animation, metadata edge cases -- a much broader mix of
/// real-world and adversarial AVIF files than the hand-built fixtures in <c>Unit/</c>) through both
/// container parsing (<see cref="Files_IdentifyGracefully"/>) and full pixel decode
/// (<see cref="Files_DecodeGracefully"/>). The latter is scoped to whatever this decoder currently supports
/// (still images -- animated AVIF, film grain, gain maps, 12-bit depth, and palette/IntraBC mode remain
/// unimplemented; see the top-level README's AVIF status entry for the exact, current boundary) -- most
/// adversarial/out-of-scope real corpus files legitimately throw <see cref="AvifUnsupportedFeatureException"/>,
/// which is a scope fact, not a failure. The ffmpeg-differential pixel comparison this doc comment used to
/// point at as future work now exists: see <c>AvifFfmpegReferenceTests</c>.
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
