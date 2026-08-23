using PeachImage.Formats.Tiff.Decoding;

namespace PeachImage.Tests.Formats.Tiff.Unit.Decoding;

public class TiffPaletteTests
{
    [Fact]
    public void Resolve_ScalesSixteenBitEntriesToEightBit()
    {
        // 2-bit palette (4 entries): black, full-red, half-intensity-green (0x8000 -> 0x80), full-blue.
        uint[] colorMap =
        [
            0, 65535, 0, 0, // R
            0, 0, 0x8000, 0, // G
            0, 0, 0, 65535, // B
        ];

        byte[] rgb = TiffPalette.Resolve(colorMap, bitsPerSample: 2);

        Assert.Equal(12, rgb.Length);
        Assert.Equal([0, 0, 0], rgb[0..3]); // entry 0: black
        Assert.Equal([255, 0, 0], rgb[3..6]); // entry 1: red
        Assert.Equal([0, 128, 0], rgb[6..9]); // entry 2: green, top-byte-truncated from 0x8000
        Assert.Equal([0, 0, 255], rgb[9..12]); // entry 3: blue
    }
}
