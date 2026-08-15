using PeachImage.Formats.Gif.Decoding;
using PeachImage.Formats.Gif.Encoding;

namespace PeachImage.Tests.Formats.Gif.Unit.Lzw;

public class GifLzwRoundTripTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    public void SolidColor_RoundTrips_Exactly(int minCodeSize)
    {
        byte[] indices = new byte[1000];
        AssertRoundTrips(indices, minCodeSize);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void Gradient_RoundTrips_Exactly(int minCodeSize)
    {
        int colorCount = 1 << minCodeSize;
        byte[] indices = new byte[5000];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (byte)(i % colorCount);
        }

        AssertRoundTrips(indices, minCodeSize);
    }

    [Fact]
    public void RandomNoise_RoundTrips_Exactly_AndForcesTableClear()
    {
        // Enough incompressible data (256-color noise) to force the code table past 4096 entries at least
        // once, exercising the encoder/decoder's forced-Clear-on-table-full path.
        var random = new Random(12345);
        byte[] indices = new byte[200_000];
        random.NextBytes(indices);

        AssertRoundTrips(indices, minCodeSize: 8);
    }

    [Fact]
    public void EmptyBuffer_RoundTrips_Exactly()
    {
        AssertRoundTrips([], minCodeSize: 8);
    }

    [Fact]
    public void SinglePixel_RoundTrips_Exactly()
    {
        AssertRoundTrips([42], minCodeSize: 8);
    }

    [Theory]
    [InlineData(253)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    public void SubBlockBoundary_RoundTrips_Exactly(int length)
    {
        byte[] indices = new byte[length];
        for (int i = 0; i < length; i++)
        {
            indices[i] = (byte)(i % 8);
        }

        AssertRoundTrips(indices, minCodeSize: 3);
    }

    private static void AssertRoundTrips(byte[] indices, int minCodeSize)
    {
        using var stream = new MemoryStream();
        GifLzwEncoder.Encode(stream, indices, minCodeSize);

        byte[] compressed = stream.ToArray();

        // The image-data sub-block stream, unwrapped, is exactly what GifLzwDecoder.Decode expects
        // (GifSubBlocks.ReadAllImageData does this unwrapping during real decode).
        using var readStream = new MemoryStream(compressed);
        byte[] imageData = ReadSubBlocks(readStream);

        byte[] decoded = GifLzwDecoder.Decode(imageData, minCodeSize, indices.Length);

        Assert.Equal(indices, decoded);
    }

    private static byte[] ReadSubBlocks(Stream stream)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            int size = stream.ReadByte();
            if (size <= 0)
            {
                break;
            }

            Span<byte> chunk = new byte[size];
            stream.ReadExactly(chunk);
            buffer.Write(chunk);
        }

        return buffer.ToArray();
    }
}
