using PeachImage.Formats.Png.Internal;

namespace PeachImage.Tests.Formats.Png.Unit.Interlacing;

public class Adam7PassGeometryTests
{
    [Fact]
    public void EightByEightImage_PassPixelCountsMatchSpecReferenceTable_AndSumToTotal()
    {
        int[] expectedPixelCounts = [1, 1, 2, 4, 8, 16, 32];

        int total = 0;
        int i = 0;
        foreach (var pass in Adam7.Passes)
        {
            var (width, height) = Adam7.GetPassDimensions(8, 8, pass);
            Assert.Equal(expectedPixelCounts[i], width * height);
            total += width * height;
            i++;
        }

        Assert.Equal(64, total);
    }

    [Fact]
    public void OnePixelImage_OnlyFirstPassContributesPixels()
    {
        bool firstPassSeen = false;
        foreach (var pass in Adam7.Passes)
        {
            var (width, height) = Adam7.GetPassDimensions(1, 1, pass);
            if (!firstPassSeen)
            {
                Assert.Equal(1, width * height);
                firstPassSeen = true;
            }
            else
            {
                Assert.Equal(0, width * height);
            }
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(17, 5)]
    public void SumOfAllPassPixelCounts_EqualsFullImagePixelCount(int width, int height)
    {
        int total = 0;
        foreach (var pass in Adam7.Passes)
        {
            var (passWidth, passHeight) = Adam7.GetPassDimensions(width, height, pass);
            total += passWidth * passHeight;
        }

        Assert.Equal(width * height, total);
    }
}
