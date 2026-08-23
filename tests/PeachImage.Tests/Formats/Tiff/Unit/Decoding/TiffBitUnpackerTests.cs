using PeachImage.Formats.Tiff.Decoding;

namespace PeachImage.Tests.Formats.Tiff.Unit.Decoding;

public class TiffBitUnpackerTests
{
    [Fact]
    public void Unpack_1Bit_MsbFirst()
    {
        byte[] row = [0b_1010_1100];
        var samples = new ushort[8];

        TiffBitUnpacker.Unpack(row, bitDepth: 1, sampleCount: 8, TiffByteOrder.LittleEndian, samples);

        Assert.Equal([1, 0, 1, 0, 1, 1, 0, 0], samples);
    }

    [Fact]
    public void Unpack_2Bit_MsbFirst()
    {
        byte[] row = [0b_00_01_10_11];
        var samples = new ushort[4];

        TiffBitUnpacker.Unpack(row, bitDepth: 2, sampleCount: 4, TiffByteOrder.LittleEndian, samples);

        Assert.Equal([0, 1, 2, 3], samples);
    }

    [Fact]
    public void Unpack_4Bit_MsbFirst()
    {
        byte[] row = [0xA5, 0x3C];
        var samples = new ushort[4];

        TiffBitUnpacker.Unpack(row, bitDepth: 4, sampleCount: 4, TiffByteOrder.LittleEndian, samples);

        Assert.Equal([0xA, 0x5, 0x3, 0xC], samples);
    }

    [Fact]
    public void Unpack_8Bit_DirectCopy()
    {
        byte[] row = [0, 1, 127, 255];
        var samples = new ushort[4];

        TiffBitUnpacker.Unpack(row, bitDepth: 8, sampleCount: 4, TiffByteOrder.LittleEndian, samples);

        Assert.Equal([0, 1, 127, 255], samples);
    }

    [Fact]
    public void Unpack_8Bit_LargeRow_ExercisesVectorizedPath()
    {
        var row = new byte[130];
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = (byte)(i * 7);
        }

        var samples = new ushort[130];
        TiffBitUnpacker.Unpack(row, bitDepth: 8, sampleCount: 130, TiffByteOrder.LittleEndian, samples);

        for (int i = 0; i < row.Length; i++)
        {
            Assert.Equal(row[i], samples[i]);
        }
    }

    [Fact]
    public void Unpack_16Bit_LittleEndian()
    {
        byte[] row = [0x34, 0x12, 0xFF, 0x00]; // 0x1234, 0x00FF little-endian.
        var samples = new ushort[2];

        TiffBitUnpacker.Unpack(row, bitDepth: 16, sampleCount: 2, TiffByteOrder.LittleEndian, samples);

        Assert.Equal([0x1234, 0x00FF], samples);
    }

    [Fact]
    public void Unpack_16Bit_BigEndian()
    {
        byte[] row = [0x12, 0x34, 0x00, 0xFF]; // 0x1234, 0x00FF big-endian.
        var samples = new ushort[2];

        TiffBitUnpacker.Unpack(row, bitDepth: 16, sampleCount: 2, TiffByteOrder.BigEndian, samples);

        Assert.Equal([0x1234, 0x00FF], samples);
    }

    [Fact]
    public void Unpack_16Bit_LargeRow_BigEndian_ExercisesVectorizedSwapPath()
    {
        int sampleCount = 40;
        var row = new byte[sampleCount * 2];
        var expected = new ushort[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            ushort value = (ushort)((i * 4001) & 0xFFFF);
            expected[i] = value;
            row[i * 2] = (byte)(value >> 8);
            row[(i * 2) + 1] = (byte)value;
        }

        var samples = new ushort[sampleCount];
        TiffBitUnpacker.Unpack(row, bitDepth: 16, sampleCount, TiffByteOrder.BigEndian, samples);

        Assert.Equal(expected, samples);
    }
}
