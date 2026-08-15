using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

public class Vp8LBackwardCopierTests
{
    [Fact]
    public void OverlappingCopy_DistanceOne_PropagatesSinglePixelForward()
    {
        // distance=1, length=5: the classic flat-color RLE case. A naive bulk Array.Copy/Span.CopyTo would
        // read the pre-copy snapshot and only duplicate the single source pixel once, leaving the rest
        // untouched/stale -- the correct behavior is every copied pixel equals the just-written one before it.
        Span<uint> pixels = [0xFF112233u, 0, 0, 0, 0, 0];
        Vp8LBackwardCopier.CopyPixels(pixels, destPos: 1, distance: 1, length: 5);

        Assert.Equal([0xFF112233u, 0xFF112233u, 0xFF112233u, 0xFF112233u, 0xFF112233u, 0xFF112233u], pixels.ToArray());
    }

    [Fact]
    public void OverlappingCopy_DistanceTwo_RepeatsTwoPixelPattern()
    {
        Span<uint> pixels = [1u, 2u, 0, 0, 0, 0, 0];
        Vp8LBackwardCopier.CopyPixels(pixels, destPos: 2, distance: 2, length: 5);

        Assert.Equal([1u, 2u, 1u, 2u, 1u, 2u, 1u], pixels.ToArray());
    }

    [Fact]
    public void OverlappingCopy_DistanceThreeeLengthSeven_RepeatsThreePixelPatternAcrossMultipleWraps()
    {
        Span<uint> pixels = [10u, 20u, 30u, 0, 0, 0, 0, 0, 0, 0];
        Vp8LBackwardCopier.CopyPixels(pixels, destPos: 3, distance: 3, length: 7);

        Assert.Equal([10u, 20u, 30u, 10u, 20u, 30u, 10u, 20u, 30u, 10u], pixels.ToArray());
    }

    [Fact]
    public void NonOverlappingCopy_DistanceGreaterThanLength_CopiesVerbatim()
    {
        Span<uint> pixels = [10u, 20u, 30u, 0, 0, 0];
        Vp8LBackwardCopier.CopyPixels(pixels, destPos: 3, distance: 3, length: 3);

        Assert.Equal([10u, 20u, 30u, 10u, 20u, 30u], pixels.ToArray());
    }

    [Fact]
    public void DistanceEqualsLength_IsTreatedAsNonOverlapping()
    {
        Span<uint> pixels = [5u, 6u, 0, 0];
        Vp8LBackwardCopier.CopyPixels(pixels, destPos: 2, distance: 2, length: 2);

        Assert.Equal([5u, 6u, 5u, 6u], pixels.ToArray());
    }
}
