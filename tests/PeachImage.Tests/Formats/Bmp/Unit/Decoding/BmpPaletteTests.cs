using PeachImage.Formats.Bmp;
using PeachImage.Formats.Bmp.Decoding;

namespace PeachImage.Tests.Formats.Bmp.Unit.Decoding;

public class BmpPaletteTests
{
    [Fact]
    public void Read_ExtremeColorsUsedAndPixelDataOffset_ClampsToBitDepthInsteadOfOverflowing()
    {
        // ColorsUsed and PixelDataOffset are both raw, attacker-controlled uints. Without a bit-depth-derived
        // clamp (an 8bpp index can never reference more than 256 palette entries), an extreme combination of
        // the two drives `entryCount * 3` past Int32.MaxValue in BmpPalette.Read, wrapping negative and
        // throwing a raw OverflowException instead of the palette being safely clamped to 256 entries.
        var header = new BmpHeader
        {
            BitCount = 8,
            PaletteEntrySize = 4,
            ColorsUsed = 0xFFFFFFFF,
            PixelDataOffset = 0xFFFFFFFF,
            HeaderBytesConsumed = 54,
        };
        using var stream = new MemoryStream(new byte[256 * 4]);

        byte[] palette = BmpPalette.Read(stream, header);

        Assert.Equal(256 * 3, palette.Length);
    }

    [Fact]
    public void Read_NormalColorsUsed_ReadsExactDeclaredCount()
    {
        var header = new BmpHeader
        {
            BitCount = 8,
            PaletteEntrySize = 4,
            ColorsUsed = 10,
            PixelDataOffset = 54 + (10 * 4),
            HeaderBytesConsumed = 54,
        };
        byte[] data = new byte[10 * 4];
        data[0] = 1; data[1] = 2; data[2] = 3; // First entry: B=1,G=2,R=3.
        using var stream = new MemoryStream(data);

        byte[] palette = BmpPalette.Read(stream, header);

        Assert.Equal(10 * 3, palette.Length);
        Assert.Equal([3, 2, 1], palette[..3]);
    }
}
