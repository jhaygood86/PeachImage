using PeachImage.Formats.Bmp.Encoding;

namespace PeachImage.Tests.Formats.Bmp.Unit.Encoding;

public class BmpRowPackerTests
{
    [Fact]
    public void PackRgb24Row_SwapsToBgrAndZeroPads()
    {
        byte[] src = [10, 20, 30]; // R, G, B for one pixel.
        Span<byte> dest = stackalloc byte[6]; // Padded to 6 bytes (width=1 -> 3 content + 3 padding).
        dest.Fill(0xFF); // Verify padding is actually overwritten with zero, not left as garbage.

        BmpRowPacker.PackRgb24Row(src, dest);

        Assert.Equal([30, 20, 10, 0, 0, 0], dest.ToArray());
    }

    [Fact]
    public void PackRgba32Row_SwapsToBgraWithNoPadding()
    {
        byte[] src = [10, 20, 30, 128]; // R, G, B, A.
        Span<byte> dest = stackalloc byte[4];

        BmpRowPacker.PackRgba32Row(src, dest);

        Assert.Equal([30, 20, 10, 128], dest.ToArray());
    }

    [Fact]
    public void PackGray8IndexedRow_CopiesDirectlyAndZeroPads()
    {
        byte[] src = [5, 200, 17];
        Span<byte> dest = stackalloc byte[4];
        dest.Fill(0xFF);

        BmpRowPacker.PackGray8IndexedRow(src, dest);

        Assert.Equal([5, 200, 17, 0], dest.ToArray());
    }

    [Theory]
    [InlineData(1, 24, 4)]
    [InlineData(5, 24, 16)]
    [InlineData(4, 24, 12)]
    [InlineData(3, 8, 4)]
    [InlineData(4, 8, 4)]
    public void GetPaddedRowByteCount_RoundsUpToFourByteBoundary(int width, int bitCount, int expected)
    {
        Assert.Equal(expected, BmpRowPacker.GetPaddedRowByteCount(width, bitCount));
    }
}
