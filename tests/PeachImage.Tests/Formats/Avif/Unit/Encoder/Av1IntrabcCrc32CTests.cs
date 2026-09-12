using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Port of libaom's own <c>hash_test.cc</c> (<c>AV1Crc32cHashTest.CheckOutput</c>/<c>CheckZero</c>):
/// deterministic random buffers (matching the real fixture's own <c>bsize * bsize * sizeof(uint16_t)</c>
/// buffer-length convention, for every real <c>hash_block_size_to_index</c> block size
/// <see cref="Av1IntrabcHashTable.BlockSizes"/> uses) compared against
/// <see cref="LibaomReferenceCrc32C"/> (an independent transcription of libaom's own real slice-by-8
/// <c>av1_get_crc32c_value_c</c>, structurally different from
/// <see cref="Av1IntrabcCrc32C.Compute"/>'s own simpler byte-at-a-time loop even though both compute the
/// same real CRC-32C).
/// </summary>
public class Av1IntrabcCrc32CTests
{
    public static TheoryData<int> BlockSizes => new(Av1IntrabcHashTable.BlockSizes);

    [Theory]
    [MemberData(nameof(BlockSizes))]
    public void Compute_MatchesLibaomReference(int bsize)
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        int length = bsize * bsize * sizeof(ushort);
        var buffer = new byte[length];
        for (int i = 0; i < length; i++)
        {
            buffer[i] = rnd.Rand8();
        }

        uint crc0 = Av1IntrabcCrc32C.Compute(buffer);
        uint crc1 = Av1IntrabcCrc32C.Compute(buffer);
        uint crc2 = LibaomReferenceCrc32C.Compute(buffer);
        Assert.Equal(crc0, crc1);
        Assert.Equal(crc0, crc2);

        buffer[0] += 1;
        uint crc3 = Av1IntrabcCrc32C.Compute(buffer);
        uint crc4 = LibaomReferenceCrc32C.Compute(buffer);
        Assert.NotEqual(crc0, crc3);
        Assert.Equal(crc3, crc4);
    }

    [Fact]
    public void Compute_ZeroBuffer_DiffersByLength()
    {
        var buffer = new byte[1024];

        uint crc0 = Av1IntrabcCrc32C.Compute(buffer.AsSpan(0, 32));
        uint crc1 = Av1IntrabcCrc32C.Compute(buffer.AsSpan(0, 128));
        uint crc2 = Av1IntrabcCrc32C.Compute(buffer.AsSpan(0, 1024));

        Assert.NotEqual(crc0, crc1);
        Assert.NotEqual(crc0, crc2);
        Assert.NotEqual(crc1, crc2);

        Assert.Equal(LibaomReferenceCrc32C.Compute(buffer.AsSpan(0, 32)), crc0);
        Assert.Equal(LibaomReferenceCrc32C.Compute(buffer.AsSpan(0, 128)), crc1);
        Assert.Equal(LibaomReferenceCrc32C.Compute(buffer.AsSpan(0, 1024)), crc2);
    }
}
