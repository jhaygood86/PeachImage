using System.Text;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1IntrabcCrc32C"/> against the standard CRC-32C (Castagnoli) check value, and
/// <see cref="Av1IntrabcHashTable"/>'s whole-frame hash-pyramid construction against a small, hand-built
/// synthetic image with a known duplicate region.
/// </summary>
public class Av1IntrabcHashTableTests
{
    [Fact]
    public void Crc32C_MatchesStandardCheckValue()
    {
        // The universally-cited CRC-32C (Castagnoli) check value: CRC32C(ASCII "123456789") == 0xE3069283
        // (init 0xFFFFFFFF, reflected in/out, final XOR 0xFFFFFFFF, poly 0x1EDC6F41 normal / 0x82F63B78
        // reversed) -- confirms Av1IntrabcCrc32C's table/algorithm is a real, standard CRC-32C, not just
        // internally self-consistent.
        byte[] data = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xE3069283u, Av1IntrabcCrc32C.Compute(data));
    }

    [Fact]
    public void Crc32C_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0u, Av1IntrabcCrc32C.Compute(ReadOnlySpan<byte>.Empty));
    }

    private static int[] BuildDuplicateRegionImage(int width, int height)
    {
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int value;
                if (x < 4 && y < 4)
                {
                    // Region A (top-left): a varying pattern, not a flat fill, so the 2x2 base level
                    // actually exercises real byte-packing rather than degenerate identical bytes.
                    value = (x * 16) + y;
                }
                else if (x >= 4 && y >= 4)
                {
                    // Region A', bottom-right: pixel-identical to region A at the same relative offset --
                    // the one duplicate this test looks for.
                    value = ((x - 4) * 16) + (y - 4);
                }
                else if (x >= 4 && y < 4)
                {
                    // Region B (top-right): a distinct flat fill.
                    value = 200;
                }
                else
                {
                    // Region C (bottom-left): a third, distinct flat fill.
                    value = 50;
                }

                pixels[(y * width) + x] = value;
            }
        }

        return pixels;
    }

    [Fact]
    public void GetCandidates_FindsDuplicateRegion_AtMatchingSize()
    {
        int[] pixels = BuildDuplicateRegionImage(8, 8);
        var table = new Av1IntrabcHashTable(pixels, 8, 8);

        var candidates = table.GetCandidates(4, 0, 0);
        Assert.NotNull(candidates);
        Assert.Contains((0, 0), candidates);
        Assert.Contains((4, 4), candidates);

        // Regions B and C are distinct fills from region A and from each other -- neither should ever
        // collide into region A's own bucket.
        Assert.DoesNotContain((4, 0), candidates);
        Assert.DoesNotContain((0, 4), candidates);
    }

    [Fact]
    public void GetCandidates_DistinctRegions_DoNotShareABucket()
    {
        int[] pixels = BuildDuplicateRegionImage(8, 8);
        var table = new Av1IntrabcHashTable(pixels, 8, 8);

        var fromB = table.GetCandidates(4, 4, 0);
        Assert.NotNull(fromB);
        Assert.DoesNotContain((0, 0), fromB);
        Assert.DoesNotContain((0, 4), fromB);
    }

    [Fact]
    public void GetCandidates_SizeAboveMaxBlockSize_ReturnsNull()
    {
        int[] pixels = BuildDuplicateRegionImage(8, 8);
        var table = new Av1IntrabcHashTable(pixels, 8, 8, maxBlockSize: 8);

        Assert.Null(table.GetCandidates(16, 0, 0));
    }

    [Fact]
    public void GetCandidates_SizeNotAnIntrabcBlockSize_ReturnsNull()
    {
        int[] pixels = BuildDuplicateRegionImage(8, 8);
        var table = new Av1IntrabcHashTable(pixels, 8, 8);

        Assert.Null(table.GetCandidates(3, 0, 0));
    }
}
