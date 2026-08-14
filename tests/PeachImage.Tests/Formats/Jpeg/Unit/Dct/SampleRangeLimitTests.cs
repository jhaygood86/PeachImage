using PeachImage.Formats.Jpeg.Dct;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Dct;

public class SampleRangeLimitTests
{
    [Fact]
    public void Table_CoversEveryMaskedIndex() => Assert.Equal(SampleRangeLimit.Mask + 1, SampleRangeLimit.Table.Length);

    [Fact]
    public void Table_MatchesClamp_AcrossTheDocumentedWindow()
    {
        for (int sample = -512; sample <= 511; sample++)
        {
            byte expected = (byte)Math.Clamp(sample, 0, 255);
            byte actual = SampleRangeLimit.Table[sample & SampleRangeLimit.Mask];
            Assert.True(expected == actual, $"sample={sample}: expected {expected}, got {actual}");
        }
    }
}
