using PeachImage.Formats.Bmp.Decoding;

namespace PeachImage.Tests.Formats.Bmp.Unit.Decoding;

public class BmpRleDecoderTests
{
    [Fact]
    public void DecodeRle8_LiteralRun_FillsRow()
    {
        byte[] data = [3, 5]; // Run of 3 pixels, value 5.

        var buffer = BmpRleDecoder.DecodeRle8(data, width: 3, height: 1);

        Assert.Equal([5, 5, 5], buffer);
    }

    [Fact]
    public void DecodeRle8_AbsoluteMode_EvenCount_NoPaddingByte()
    {
        byte[] data = [0, 4, 10, 20, 30, 40];

        var buffer = BmpRleDecoder.DecodeRle8(data, width: 4, height: 1);

        Assert.Equal([10, 20, 30, 40], buffer);
    }

    [Fact]
    public void DecodeRle8_AbsoluteMode_OddCount_ConsumesPaddingByte()
    {
        byte[] data = [0, 3, 10, 20, 30, 99]; // 99 is the alignment padding byte, not a pixel.

        var buffer = BmpRleDecoder.DecodeRle8(data, width: 3, height: 1);

        Assert.Equal([10, 20, 30], buffer);
    }

    [Fact]
    public void DecodeRle8_Delta_SkipsToNewPosition()
    {
        byte[] data = [0, 2, 2, 1, 5, 7]; // Delta (dx=2,dy=1), then a run of 5 pixels value 7.

        var buffer = BmpRleDecoder.DecodeRle8(data, width: 5, height: 3);

        Assert.Equal(0, buffer[(1 * 5) + 0]);
        Assert.Equal(0, buffer[(1 * 5) + 1]);
        Assert.Equal(7, buffer[(1 * 5) + 2]);
        Assert.Equal(7, buffer[(1 * 5) + 3]);
        Assert.Equal(7, buffer[(1 * 5) + 4]);
    }

    [Fact]
    public void DecodeRle8_EndOfLine_AdvancesRowAndResetsColumn()
    {
        byte[] data = [2, 9, 0, 0, 2, 4]; // Run(2,9), EOL, run(2,4).

        var buffer = BmpRleDecoder.DecodeRle8(data, width: 3, height: 2);

        Assert.Equal([9, 9, 0, 4, 4, 0], buffer);
    }

    [Fact]
    public void DecodeRle8_TruncatedAbsoluteMode_StopsGracefullyWithoutThrowing()
    {
        byte[] data = [0, 5, 1, 2, 3]; // Declares 5 literal bytes but only 3 are present.

        var buffer = BmpRleDecoder.DecodeRle8(data, width: 5, height: 1);

        Assert.Equal([1, 2, 3, 0, 0], buffer);
    }

    [Fact]
    public void DecodeRle8_RunOverflowingRowWidth_ClampsWithoutCorruptingNextRow()
    {
        byte[] data = [10, 5]; // Run of 10 pixels, but width is only 3 — no EOL marker.

        var buffer = BmpRleDecoder.DecodeRle8(data, width: 3, height: 2);

        Assert.Equal([5, 5, 5, 0, 0, 0], buffer);
    }

    [Fact]
    public void DecodeRle4_LiteralRun_AlternatesHighAndLowNibble()
    {
        byte[] data = [5, 0xAB]; // 5 pixels alternating hi(0xA)/lo(0xB) nibbles.

        var buffer = BmpRleDecoder.DecodeRle4(data, width: 5, height: 1);

        Assert.Equal([0xA, 0xB, 0xA, 0xB, 0xA], buffer);
    }

    [Fact]
    public void DecodeRle4_AbsoluteMode_EvenByteCount_NoPaddingByte()
    {
        byte[] data = [0, 3, 0x12, 0x34]; // 3 pixels from 2 bytes (even byte count, no padding).

        var buffer = BmpRleDecoder.DecodeRle4(data, width: 3, height: 1);

        Assert.Equal([1, 2, 3], buffer);
    }

    [Fact]
    public void DecodeRle4_AbsoluteMode_OddByteCount_ConsumesPaddingByte()
    {
        byte[] data = [0, 5, 0x12, 0x34, 0x56, 0x99]; // 5 pixels from 3 bytes (odd, plus 1 padding byte).

        var buffer = BmpRleDecoder.DecodeRle4(data, width: 5, height: 1);

        Assert.Equal([1, 2, 3, 4, 5], buffer);
    }

    [Fact]
    public void DecodeRle4_EndOfBitmap_StopsDecoding()
    {
        byte[] data = [2, 0x11, 0, 1, 2, 0x22]; // Run, then EOB — trailing bytes must be ignored.

        var buffer = BmpRleDecoder.DecodeRle4(data, width: 2, height: 2);

        Assert.Equal([1, 1, 0, 0], buffer);
    }
}
