using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

public class Vp8LColorCacheTests
{
    [Fact]
    public void Insert_ThenLookupByComputedHashIndex_ReturnsInsertedValue()
    {
        var cache = new Vp8LColorCache(cacheBits: 4);
        uint argb = 0xFF112233u;
        cache.Insert(argb);

        int index = ComputeHash(argb, 4);
        Assert.Equal(argb, cache.Lookup(index));
    }

    [Fact]
    public void Insert_OverwritesWhateverPreviouslyOccupiedTheSameSlot()
    {
        var cache = new Vp8LColorCache(cacheBits: 1); // only 2 slots.
        uint first = 0xFF000000u;
        int index = ComputeHash(first, 1);
        cache.Insert(first);
        Assert.Equal(first, cache.Lookup(index));

        // Any value that hashes to the same slot must overwrite the first (a plain, non-chaining cache).
        uint second = FindValueHashingTo(index, 1, avoid: first);
        cache.Insert(second);
        Assert.Equal(second, cache.Lookup(index));
    }

    [Fact]
    public void MultipleDistinctValues_EachRetrievableAtItsOwnHashIndex()
    {
        var cache = new Vp8LColorCache(cacheBits: 8); // 256 slots -- collisions unlikely for a handful of values.
        uint[] values = [0xFFAABBCCu, 0x80102030u, 0x00FFFFFFu, 0xFF000000u, 0x11223344u];
        var indices = new int[values.Length];

        for (int i = 0; i < values.Length; i++)
        {
            cache.Insert(values[i]);
            indices[i] = ComputeHash(values[i], 8);
        }

        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], cache.Lookup(indices[i]));
        }
    }

    private static int ComputeHash(uint argb, int cacheBits)
    {
        const uint hashMultiplier = 0x1e35a7bdu;
        return (int)((argb * hashMultiplier) >> (32 - cacheBits));
    }

    private static uint FindValueHashingTo(int targetIndex, int cacheBits, uint avoid)
    {
        for (uint candidate = 0; candidate < 1_000_000; candidate++)
        {
            if (candidate != avoid && ComputeHash(candidate, cacheBits) == targetIndex)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No colliding value found within the search range.");
    }
}
