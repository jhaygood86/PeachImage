using PeachImage.Formats.Jxl;
using PeachImage.Formats.Jxl.Bitstream;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlBitstreamTests
{
    [Fact]
    public void ReadBits_IsLsbFirstAcrossByteBoundaries()
    {
        byte[] data = [0b1010_1100, 0b0000_0011];
        var reader = new JxlBitReader(data);

        Assert.Equal(0b00u, reader.ReadBits(2));
        Assert.Equal(0b1011u, reader.ReadBits(4));
        Assert.Equal(14u, reader.ReadBits(6)); // bits 6..11 straddle the byte boundary
        Assert.False(reader.IsOverrun);
    }

    [Fact]
    public void ReadBits_PastEnd_LatchesOverrunAndYieldsZeros()
    {
        byte[] data = [0xFF];
        var reader = new JxlBitReader(data);

        Assert.Equal(0xFFu, reader.ReadBits(8));
        Assert.False(reader.IsOverrun);
        Assert.Equal(0u, reader.ReadBits(3));
        Assert.True(reader.IsOverrun);
        Assert.Throws<JxlDecodingException>(() =>
        {
            var r = new JxlBitReader(data);
            r.ReadBits(8);
            r.ReadBits(1);
            r.ThrowIfOverrun();
        });
    }

    [Fact]
    public void ReadBits32_ReturnsAllThirtyTwoBits()
    {
        byte[] data = [0x78, 0x56, 0x34, 0x12, 0xAA];
        var reader = new JxlBitReader(data);
        Assert.Equal(0x12345678u, reader.ReadBits(32));
    }

    [Fact]
    public void ZeroPadToByte_RejectsNonZeroPadding()
    {
        byte[] data = [0b0000_0111];
        Assert.Throws<JxlDecodingException>(() =>
        {
            var reader = new JxlBitReader(data);
            reader.ReadBits(2);
            reader.ZeroPadToByte();
        });

        var ok = new JxlBitReader((byte[])[0b0000_0011]);
        ok.ReadBits(2);
        ok.ZeroPadToByte();
        Assert.Equal(8, ok.BitPosition);
    }

    [Fact]
    public void ReadU64_ShortSelectors()
    {
        var zero = new JxlBitReader((byte[])[0b00]);
        Assert.Equal(0UL, JxlFieldReader.ReadU64(ref zero));

        // selector 1 + 4 bits (5) → 1 + 5.
        var one = new JxlBitReader((byte[])[(byte)(0b01 | (5 << 2))]);
        Assert.Equal(6UL, JxlFieldReader.ReadU64(ref one));

        // selector 2 + 8 bits (0xAB) → 17 + 0xAB.
        var two = new JxlBitReader((byte[])[0xAE, 0x02]);
        Assert.Equal(17UL + 0xAB, JxlFieldReader.ReadU64(ref two));
    }

    [Fact]
    public void ReadU64_VarintSelector_ReadsContinuationGroups()
    {
        // selector 3, 12 bits = 0x123, continuation 1, 8 bits = 0x45, continuation 0.
        // Bit layout (LSB first): 11 | 0x123 (12) | 1 | 0x45 (8) | 0
        ulong bits = 0b11UL | (0x123UL << 2) | (1UL << 14) | (0x45UL << 15) | (0UL << 23);
        byte[] data = [(byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24)];
        var reader = new JxlBitReader(data);

        Assert.Equal(0x123UL | (0x45UL << 12), JxlFieldReader.ReadU64(ref reader));
        Assert.Equal(24, reader.BitPosition);
    }

    [Fact]
    public void ReadU32_SelectsDistributionAndAppliesOffset()
    {
        // selector 2 → BitsOffset(4, 2); payload 5 → 7.
        byte[] data = [(byte)(0b10 | (5 << 2))];
        var reader = new JxlBitReader(data);
        uint value = JxlFieldReader.ReadU32(ref reader, U32Dist.Val(0), U32Dist.Val(1), U32Dist.BitsOffset(4, 2), U32Dist.BitsOffset(6, 18));
        Assert.Equal(7u, value);
    }

    [Fact]
    public void ReadF16_DecodesHalfPrecisionAndRejectsInfinity()
    {
        // 1.5 = 0x3E00
        var reader = new JxlBitReader((byte[])[0x00, 0x3E]);
        Assert.Equal(1.5f, JxlFieldReader.ReadF16(ref reader));

        Assert.Throws<JxlDecodingException>(() =>
        {
            var r = new JxlBitReader((byte[])[0x00, 0x7C]);
            JxlFieldReader.ReadF16(ref r);
        });
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, -1)]
    [InlineData(2u, 1)]
    [InlineData(3u, -2)]
    [InlineData(4u, 2)]
    public void UnpackSigned_MatchesZigzag(uint packed, int expected) =>
        Assert.Equal(expected, JxlFieldReader.UnpackSigned(packed));
}
