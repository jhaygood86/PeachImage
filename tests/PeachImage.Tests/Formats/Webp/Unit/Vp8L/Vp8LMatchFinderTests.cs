using PeachImage.Formats.Webp.Encoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

/// <summary>Correctness tests for <see cref="Vp8LMatchFinder"/>: every reported match must be real (verified by direct comparison against the source pixels), in-bounds, and within the format's own length/distance ceilings.</summary>
public class Vp8LMatchFinderTests
{
    [Fact]
    public void TryFindMatch_FindsRepeatedPattern()
    {
        uint[] pixels = new uint[300];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (uint)(0x11223300 + (i % 3));
        }

        var finder = new Vp8LMatchFinder(pixels, pixels.Length, hashBits: 12, maxChainLength: 32, giveUpAfterFruitlessProbes: 32);
        bool foundAtLeastOneMatch = false;

        for (int pos = 0; pos < pixels.Length; pos++)
        {
            if (finder.TryFindMatch(pos, out int distance, out int length))
            {
                foundAtLeastOneMatch = true;
                Assert.True(distance >= 1);
                Assert.True(length >= 3);
                Assert.True(pos - distance >= 0);
                Assert.True(pos + length <= pixels.Length);

                for (int i = 0; i < length; i++)
                {
                    Assert.Equal(pixels[pos - distance + i], pixels[pos + i]);
                }
            }

            finder.Insert(pos);
        }

        Assert.True(foundAtLeastOneMatch);
    }

    [Fact]
    public void TryFindMatch_NeverExceedsFormatCeilings_OnALongFlatRun()
    {
        uint[] pixels = new uint[10_000];
        Array.Fill(pixels, 0xFF00FF00u);

        var finder = new Vp8LMatchFinder(pixels, pixels.Length, hashBits: 14, maxChainLength: 64, giveUpAfterFruitlessProbes: 64);

        for (int pos = 0; pos < pixels.Length; pos++)
        {
            if (finder.TryFindMatch(pos, out int distance, out int length))
            {
                Assert.True(length <= Vp8LEncodingLimits.MaxBackwardReferenceLength);
                Assert.True(distance <= Vp8LEncodingLimits.MaxBackwardReferenceDistance);
                Assert.True(pos + length <= pixels.Length);
            }

            finder.Insert(pos);
        }
    }

    [Fact]
    public void TryFindMatch_ReturnsFalse_WhenNoRepeatExists()
    {
        uint[] pixels = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var finder = new Vp8LMatchFinder(pixels, pixels.Length, hashBits: 10, maxChainLength: 16, giveUpAfterFruitlessProbes: 16);

        for (int pos = 0; pos < pixels.Length; pos++)
        {
            Assert.False(finder.TryFindMatch(pos, out _, out _));
            finder.Insert(pos);
        }
    }

    /// <summary>
    /// Plants a genuine match buried deep in a hash chain behind many unrelated same-bucket entries (forced
    /// via a 1-bit-wide hash table, so roughly half of all insertions collide into the match's bucket). A
    /// generous <c>giveUpAfterFruitlessProbes</c> must still find it; a stingy one must give up before
    /// reaching it -- proving the early-exit heuristic actually changes search behavior, not just that it's
    /// harmless when never triggered.
    /// </summary>
    [Fact]
    public void TryFindMatch_GivesUpEarly_MissesADeeplyBuriedMatch_WhenFruitlessProbeBudgetIsLow()
    {
        uint[] pixels = new uint[43];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (uint)(1000 + i);
        }

        // Plant a genuine 3-pixel match: [40,41,42] equal [0,1,2] exactly, so they hash identically.
        pixels[40] = pixels[0];
        pixels[41] = pixels[1];
        pixels[42] = pixels[2];

        Assert.True(FindsMatchAt40(pixels, giveUpAfterFruitlessProbes: 40));
        Assert.False(FindsMatchAt40(pixels, giveUpAfterFruitlessProbes: 2));

        static bool FindsMatchAt40(uint[] pixels, int giveUpAfterFruitlessProbes)
        {
            var finder = new Vp8LMatchFinder(pixels, pixels.Length, hashBits: 1, maxChainLength: 40, giveUpAfterFruitlessProbes);
            for (int pos = 0; pos < 40; pos++)
            {
                finder.Insert(pos);
            }

            return finder.TryFindMatch(40, out _, out _);
        }
    }
}
