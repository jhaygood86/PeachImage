namespace PeachImage.Tests.Formats.Png.Corpus;

/// <summary>
/// Direct, offline coverage of <see cref="PngSuiteFileName.Classify"/> against literal example filenames
/// from the real PngSuite corpus, so a bucketing regression is caught even when the network-dependent
/// corpus itself is unavailable (in which case <see cref="CorpusFileSource"/>'s MemberData sources
/// silently yield zero cases, which would otherwise hide a parser bug).
/// </summary>
public class PngSuiteFileNameTests
{
    [Theory]
    [InlineData("basn0g01.png", PngSuiteBucket.Valid)]
    [InlineData("basi6a16.png", PngSuiteBucket.Valid)]
    [InlineData("tbrn2c08.png", PngSuiteBucket.Valid)]
    [InlineData("g25n2c08.png", PngSuiteBucket.Valid)]
    [InlineData("s01i3p01.png", PngSuiteBucket.Valid)]
    [InlineData("z09n2c08.png", PngSuiteBucket.Valid)]
    [InlineData("exif2c08.png", PngSuiteBucket.Valid)] // 4-character test id ("exif"), not the usual 3.
    [InlineData("xhdn0g08.png", PngSuiteBucket.Invalid)]
    [InlineData("xcrn0g04.png", PngSuiteBucket.Invalid)]
    [InlineData("xs1n0g01.png", PngSuiteBucket.Invalid)]
    [InlineData("PngSuite.png", PngSuiteBucket.Excluded)]
    [InlineData("README", PngSuiteBucket.Excluded)]
    public void Classify_MatchesExpectedBucket(string fileName, PngSuiteBucket expected)
    {
        Assert.Equal(expected, PngSuiteFileName.Classify(fileName));
    }
}
