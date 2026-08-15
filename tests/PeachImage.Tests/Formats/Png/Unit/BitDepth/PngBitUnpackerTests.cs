using PeachImage.Formats.Png.Decoding;

namespace PeachImage.Tests.Formats.Png.Unit.BitDepth;

public class PngBitUnpackerTests
{
    [Fact]
    public void Unpack_BitDepth1_UnpacksMsbFirst()
    {
        byte[] row = [0b1010_0000];
        var samples = new ushort[8];

        PngBitUnpacker.Unpack(row, bitDepth: 1, pixelCount: 8, samplesPerPixel: 1, samples);

        Assert.Equal([1, 0, 1, 0, 0, 0, 0, 0], samples);
    }

    [Fact]
    public void Unpack_BitDepth2_UnpacksMsbFirst()
    {
        byte[] row = [0b11_00_10_01];
        var samples = new ushort[4];

        PngBitUnpacker.Unpack(row, bitDepth: 2, pixelCount: 4, samplesPerPixel: 1, samples);

        Assert.Equal([3, 0, 2, 1], samples);
    }

    [Fact]
    public void Unpack_BitDepth4_UnpacksMsbFirst()
    {
        byte[] row = [0xAB];
        var samples = new ushort[2];

        PngBitUnpacker.Unpack(row, bitDepth: 4, pixelCount: 2, samplesPerPixel: 1, samples);

        Assert.Equal([0xA, 0xB], samples);
    }

    [Fact]
    public void Unpack_BitDepth8_IsDirectPassthrough()
    {
        byte[] row = [0, 128, 255];
        var samples = new ushort[3];

        PngBitUnpacker.Unpack(row, bitDepth: 8, pixelCount: 3, samplesPerPixel: 1, samples);

        Assert.Equal([0, 128, 255], samples);
    }

    [Fact]
    public void Unpack_BitDepth16_ReadsBigEndian()
    {
        byte[] row = [0x01, 0x02, 0xFF, 0xFF];
        var samples = new ushort[2];

        PngBitUnpacker.Unpack(row, bitDepth: 16, pixelCount: 2, samplesPerPixel: 1, samples);

        Assert.Equal([0x0102, 0xFFFF], samples);
    }

    [Fact]
    public void Unpack_MultipleSamplesPerPixel_InterleavesCorrectly()
    {
        // Bit depth 8, 3 samples per pixel (e.g. truecolor), 2 pixels.
        byte[] row = [10, 20, 30, 40, 50, 60];
        var samples = new ushort[6];

        PngBitUnpacker.Unpack(row, bitDepth: 8, pixelCount: 2, samplesPerPixel: 3, samples);

        Assert.Equal([10, 20, 30, 40, 50, 60], samples);
    }

    [Fact]
    public void Unpack_BitDepth1_IgnoresRowTailPaddingBits()
    {
        // Width 3 at depth 1: only the top 3 bits of the byte are meaningful; the rest is padding.
        byte[] row = [0b101_00000];
        var samples = new ushort[3];

        PngBitUnpacker.Unpack(row, bitDepth: 1, pixelCount: 3, samplesPerPixel: 1, samples);

        Assert.Equal([1, 0, 1], samples);
    }
}
