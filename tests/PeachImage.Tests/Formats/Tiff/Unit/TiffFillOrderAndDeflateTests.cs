using System.IO.Compression;
using PeachImage.Formats.Tiff;

namespace PeachImage.Tests.Formats.Tiff.Unit;

public class TiffFillOrderAndDeflateTests
{
    private static readonly byte[] Pixels = [0, 1, 2, 3, 0x80, 0x40, 0xF0, 0xFF];

    [Fact]
    public void LsbFirstFillOrder_UncompressedData_DecodesSameAsMsbFirst()
    {
        byte[] reversed = Pixels.Select(ReverseBits).ToArray();

        byte[] expected = Decode(Build(compression: 1, strip: Pixels));
        byte[] actual = Decode(Build(compression: 1, strip: reversed, fillOrder: 2));

        Assert.Equal(Pixels, expected);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(32946)]
    public void Deflate_DecodesSameAsUncompressed(int compression)
    {
        byte[] actual = Decode(Build(compression, Deflate(Pixels)));

        Assert.Equal(Pixels, actual);
    }

    [Fact]
    public void Deflate_WithHorizontalPredictor_UndoesPredictor()
    {
        byte[] differenced = new byte[Pixels.Length];
        for (int row = 0; row < 2; row++)
        {
            for (int x = 0; x < 4; x++)
            {
                int i = (row * 4) + x;
                differenced[i] = x == 0 ? Pixels[i] : (byte)(Pixels[i] - Pixels[i - 1]);
            }
        }

        byte[] actual = Decode(Build(8, Deflate(differenced), predictor: 2));

        Assert.Equal(Pixels, actual);
    }

    [Fact]
    public void Deflate_CorruptStream_ThrowsTiffDecodingException()
    {
        byte[] corrupt = [0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

        Assert.Throws<TiffDecodingException>(() => Decode(Build(8, corrupt)));
    }

    private static byte[] Build(int compression, byte[] strip, int? fillOrder = null, int predictor = 1) => new TiffFixtureBuilder
    {
        Width = 4,
        Height = 2,
        Compression = compression,
        FillOrder = fillOrder,
        Predictor = predictor,
        Strips = [strip],
    }.Build();

    private static byte[] Decode(byte[] file)
    {
        using var image = Image.Load(new MemoryStream(file));
        return image.GetPixelSpan().ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static byte ReverseBits(byte b)
    {
        int r = 0;
        for (int i = 0; i < 8; i++)
        {
            r |= ((b >> i) & 1) << (7 - i);
        }

        return (byte)r;
    }
}
