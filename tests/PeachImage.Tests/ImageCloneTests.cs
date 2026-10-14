namespace PeachImage.Tests;

public class ImageCloneTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Clone_PreservesHasAlphaAndIsAnimated(bool hasAlpha, bool isAnimated)
    {
        using var source = Image.Create(2, 2, PixelFormat.Rgba32);
        source.HasAlpha = hasAlpha;
        source.IsAnimated = isAnimated;

        using var clone = source.Clone();

        Assert.Equal(hasAlpha, clone.HasAlpha);
        Assert.Equal(isAnimated, clone.IsAnimated);
    }
}
