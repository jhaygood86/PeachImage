using PeachImage.Formats.Tiff.Decoding;

namespace PeachImage.Tests.Formats.Tiff.Unit.Decoding;

public class TiffPredictorTests
{
    [Fact]
    public void UndoHorizontalDifferencing8_Grayscale_ReconstructsOriginalRow()
    {
        // Original: 10, 20, 15, 40, 40. Differenced (samplesPerPixel=1): 10, 10, -5(=251), 25, 0.
        byte[] row = [10, 10, 251, 25, 0];

        TiffPredictor.UndoHorizontalDifferencing8(row, samplesPerPixel: 1);

        Assert.Equal([10, 20, 15, 40, 40], row);
    }

    [Fact]
    public void UndoHorizontalDifferencing8_Rgb_ReconstructsPerChannel()
    {
        // Two RGB pixels: (10,20,30), (15,25,35). Differenced per channel (stride 3): (10,20,30), (5,5,5).
        byte[] row = [10, 20, 30, 5, 5, 5];

        TiffPredictor.UndoHorizontalDifferencing8(row, samplesPerPixel: 3);

        Assert.Equal([10, 20, 30, 15, 25, 35], row);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UndoHorizontalDifferencing16_Grayscale_ReconstructsOriginalRow(bool littleEndian)
    {
        var byteOrder = littleEndian ? TiffByteOrder.LittleEndian : TiffByteOrder.BigEndian;

        // Original 16-bit samples: 1000, 2000, 1500. Differenced: 1000, 1000, -500 (=65036 wrapped).
        byte[] row = new byte[6];
        WriteSample(row, 0, 1000, byteOrder);
        WriteSample(row, 2, 1000, byteOrder);
        WriteSample(row, 4, 65036, byteOrder);

        TiffPredictor.UndoHorizontalDifferencing16(row, samplesPerPixel: 1, byteOrder);

        Assert.Equal(1000, ReadSample(row, 0, byteOrder));
        Assert.Equal(2000, ReadSample(row, 2, byteOrder));
        Assert.Equal(1500, ReadSample(row, 4, byteOrder));
    }

    private static void WriteSample(Span<byte> row, int offset, ushort value, TiffByteOrder byteOrder)
    {
        if (byteOrder == TiffByteOrder.LittleEndian)
        {
            row[offset] = (byte)value;
            row[offset + 1] = (byte)(value >> 8);
        }
        else
        {
            row[offset] = (byte)(value >> 8);
            row[offset + 1] = (byte)value;
        }
    }

    private static int ReadSample(ReadOnlySpan<byte> row, int offset, TiffByteOrder byteOrder) =>
        byteOrder == TiffByteOrder.LittleEndian
            ? row[offset] | (row[offset + 1] << 8)
            : (row[offset] << 8) | row[offset + 1];
}
