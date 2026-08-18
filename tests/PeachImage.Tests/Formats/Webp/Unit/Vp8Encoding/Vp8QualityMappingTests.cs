using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8Encoding;

public class Vp8QualityMappingTests
{
    [Theory]
    [InlineData(0, 127)]
    [InlineData(100, 0)]
    [InlineData(-10, 127)]
    [InlineData(110, 0)]
    public void QualityToBaseQIndex_ClampsToValidRange(int quality, int expected)
    {
        Assert.Equal(expected, Vp8QualityMapping.QualityToBaseQIndex(quality));
    }

    [Fact]
    public void QualityToBaseQIndex_IsMonotonicallyDecreasing()
    {
        int previous = Vp8QualityMapping.QualityToBaseQIndex(0);
        for (int quality = 1; quality <= 100; quality++)
        {
            int current = Vp8QualityMapping.QualityToBaseQIndex(quality);
            Assert.True(current <= previous, $"Expected quality {quality}'s Q-index ({current}) <= quality {quality - 1}'s ({previous}).");
            previous = current;
        }
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(100, 0)]
    [InlineData(-10, 50)]
    [InlineData(110, 0)]
    public void QualityToFilterLevel_ClampsToValidRange(int quality, int expected)
    {
        Assert.Equal(expected, Vp8QualityMapping.QualityToFilterLevel(quality));
    }

    [Fact]
    public void QualityToFilterLevel_IsMonotonicallyDecreasing()
    {
        int previous = Vp8QualityMapping.QualityToFilterLevel(0);
        for (int quality = 1; quality <= 100; quality++)
        {
            int current = Vp8QualityMapping.QualityToFilterLevel(quality);
            Assert.True(current <= previous, $"Expected quality {quality}'s filter level ({current}) <= quality {quality - 1}'s ({previous}).");
            previous = current;
        }
    }

    [Fact]
    public void QualityToBaseQIndex_AlwaysWithinValidVp8Range()
    {
        for (int quality = 0; quality <= 100; quality++)
        {
            int q = Vp8QualityMapping.QualityToBaseQIndex(quality);
            Assert.InRange(q, 0, 127);
        }
    }

    [Fact]
    public void QualityToFilterLevel_AlwaysWithinValidVp8Range()
    {
        for (int quality = 0; quality <= 100; quality++)
        {
            int level = Vp8QualityMapping.QualityToFilterLevel(quality);
            Assert.InRange(level, 0, 63);
        }
    }
}
