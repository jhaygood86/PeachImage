using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

public class Vp8LBitReaderTests
{
    [Fact]
    public void ReadBits_ExtractsLeastSignificantBitFirst()
    {
        // 0xB4 = 0b1011_0100; LSB-first single-bit reads should yield: 0,0,1,0,1,1,0,1
        byte[] data = [0xB4];
        var reader = new Vp8LBitReader(data, 0, data.Length);

        uint[] expected = [0, 0, 1, 0, 1, 1, 0, 1];
        foreach (uint bit in expected)
        {
            Assert.Equal(bit, reader.ReadBits(1));
        }
    }

    [Fact]
    public void PeekBits_DoesNotConsumeUntilSkipOrRead()
    {
        byte[] data = [0xFF, 0x00];
        var reader = new Vp8LBitReader(data, 0, data.Length);

        Assert.Equal(0xFFu, reader.PeekBits(8));
        Assert.Equal(0xFFu, reader.PeekBits(8));
        Assert.Equal(0xFFu, reader.ReadBits(8));
        Assert.Equal(0u, reader.PeekBits(8));
    }

    [Fact]
    public void WideRead_MatchesBitByBitReference()
    {
        byte[] data = [0x12, 0x34, 0x56, 0x78, 0x9A];
        var reader = new Vp8LBitReader(data, 0, data.Length);

        uint wide = reader.ReadBits(24);
        uint reference = ReadBitsReference(data, bitOffset: 0, bitCount: 24);

        Assert.Equal(reference, wide);
    }

    [Fact]
    public void BulkRefill_AgreesWithBitByBitReference_AcrossManyByteBoundaries()
    {
        // 40 bytes: enough to force several 8-byte bulk refills. 19-bit reads (not a divisor of 8 or 64)
        // guarantee every read straddles a byte boundary somewhere, and repeated reads eventually straddle
        // an 8-byte bulk-refill boundary too.
        var random = new Random(1234);
        byte[] data = new byte[40];
        random.NextBytes(data);

        var reader = new Vp8LBitReader(data, 0, data.Length);
        for (int i = 0; i < 16; i++)
        {
            uint actual = reader.ReadBits(19);
            uint reference = ReadBitsReference(data, i * 19, 19);
            Assert.Equal(reference, actual);
        }
    }

    [Fact]
    public void TailByteFallback_AgreesWithBitByBitReference_WhenMisaligned()
    {
        // Consuming a single bit first forces every subsequent read off the 8-byte-aligned bulk-refill fast
        // path and onto the byte-at-a-time fallback for a while -- must still agree with the reference.
        var random = new Random(5678);
        byte[] data = new byte[24];
        random.NextBytes(data);

        var reader = new Vp8LBitReader(data, 0, data.Length);
        reader.ReadBits(1);

        for (int i = 0; i < 10; i++)
        {
            uint actual = reader.ReadBits(17);
            uint reference = ReadBitsReference(data, 1 + (i * 17), 17);
            Assert.Equal(reference, actual);
        }
    }

    [Fact]
    public void EndOfStream_SynthesizesZeroBits_WithoutThrowing()
    {
        byte[] data = [0xFF];
        var reader = new Vp8LBitReader(data, 0, data.Length);

        reader.ReadBits(8);
        uint padded = reader.ReadBits(16);

        Assert.Equal(0u, padded);
    }

    [Fact]
    public void IsOverBudget_FalseWhileWithinRealData_TrueOncePastIt()
    {
        byte[] data = [0xFF];
        var reader = new Vp8LBitReader(data, 0, data.Length);

        Assert.False(reader.IsOverBudget);
        reader.ReadBits(8);
        Assert.False(reader.IsOverBudget);

        reader.ReadBits(1);
        Assert.True(reader.IsOverBudget);
    }

    [Fact]
    public void StartOffset_SkipsLeadingBytes()
    {
        byte[] data = [0x00, 0xFF];
        var reader = new Vp8LBitReader(data, startOffset: 1, endOffset: data.Length);

        Assert.Equal(0xFFu, reader.ReadBits(8));
    }

    /// <summary>
    /// Drives long randomized sequences of interleaved <c>PeekBits</c>/<c>SkipBits</c>/<c>ReadBits</c> against
    /// a bit-by-bit reference that tracks the same position independently, over buffers short enough that the
    /// zero-padded tail is reached often.
    /// </summary>
    /// <remarks>
    /// The targeted tests above each pin one path; this is what covers the *combinations* — in particular the
    /// arbitrary, never-byte-aligned register states that arise when Huffman codes of odd lengths are skipped
    /// one after another, which is exactly the regime the bulk refill has to be correct in and the regime the
    /// old <c>_bitCount == 0</c>-gated bulk load never actually ran in.
    /// </remarks>
    [Fact]
    public void MixedPeekSkipReadSequences_AgreeWithBitByBitReference()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            var random = new Random(seed);
            byte[] data = new byte[random.Next(1, 40)];
            random.NextBytes(data);

            var reader = new Vp8LBitReader(data, 0, data.Length);
            int bitOffset = 0;

            for (int step = 0; step < 400; step++)
            {
                int bitCount = random.Next(0, 33);
                uint expected = ReadBitsReference(data, bitOffset, bitCount);

                switch (random.Next(3))
                {
                    case 0:
                        Assert.Equal(expected, reader.PeekBits(bitCount));
                        break;

                    case 1:
                        Assert.Equal(expected, reader.PeekBits(bitCount));
                        reader.SkipBits(bitCount);
                        bitOffset += bitCount;
                        break;

                    default:
                        Assert.Equal(expected, reader.ReadBits(bitCount));
                        bitOffset += bitCount;
                        break;
                }

                Assert.Equal(bitOffset > data.Length * 8, reader.IsOverBudget);
            }
        }
    }

    private static uint ReadBitsReference(byte[] data, int bitOffset, int bitCount)
    {
        uint value = 0;
        for (int i = 0; i < bitCount; i++)
        {
            int bitIndex = bitOffset + i;
            int byteIndex = bitIndex / 8;
            int bitInByte = bitIndex % 8;
            int bit = byteIndex < data.Length ? (data[byteIndex] >> bitInByte) & 1 : 0;
            value |= (uint)bit << i;
        }

        return value;
    }
}
