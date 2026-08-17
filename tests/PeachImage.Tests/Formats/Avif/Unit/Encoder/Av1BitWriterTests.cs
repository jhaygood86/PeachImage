using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1BitWriter"/> by round-tripping through the existing, already-correct
/// <see cref="Av1BitReader"/> -- the strongest available oracle for a byte-framing writer, since the
/// reader was itself validated independently (hand-traced vectors, corpus decode) before this writer
/// existed.
/// </summary>
public class Av1BitWriterTests
{
    [Theory]
    [InlineData(0u, 1)]
    [InlineData(1u, 1)]
    [InlineData(0u, 8)]
    [InlineData(255u, 8)]
    [InlineData(0x1u, 4)]
    [InlineData(0xFu, 4)]
    [InlineData(0x5A5u, 12)]
    public void WriteBits_RoundTripsThroughReader(uint value, int bits)
    {
        var writer = new Av1BitWriter();
        writer.WriteBits(value, bits);

        var reader = new Av1BitReader(writer.ToArray(), 0, writer.ToArray().Length);
        Assert.Equal(value, reader.ReadBits(bits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteFlag_RoundTripsThroughReader(bool value)
    {
        var writer = new Av1BitWriter();
        writer.WriteFlag(value);

        var bytes = writer.ToArray();
        var reader = new Av1BitReader(bytes, 0, bytes.Length);
        Assert.Equal(value, reader.ReadFlag());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(6u)]
    [InlineData(7u)]
    [InlineData(100u)]
    [InlineData(1_000_000u)]
    public void WriteUvlc_RoundTripsThroughReader(uint value)
    {
        var writer = new Av1BitWriter();
        writer.WriteUvlc(value);

        var bytes = writer.ToArray();
        var reader = new Av1BitReader(bytes, 0, bytes.Length);
        Assert.Equal(value, reader.ReadUvlc());
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(7, 4)]
    [InlineData(-8, 4)]
    [InlineData(-1, 4)]
    [InlineData(1, 8)]
    [InlineData(-128, 8)]
    [InlineData(127, 8)]
    public void WriteSu_RoundTripsThroughReader(int value, int bits)
    {
        var writer = new Av1BitWriter();
        writer.WriteSu(value, bits);

        var bytes = writer.ToArray();
        var reader = new Av1BitReader(bytes, 0, bytes.Length);
        Assert.Equal(value, reader.ReadSu(bits));
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(0u, 3u)]
    [InlineData(1u, 3u)]
    [InlineData(2u, 3u)]
    [InlineData(0u, 5u)]
    [InlineData(4u, 5u)]
    [InlineData(0u, 255u)]
    [InlineData(254u, 255u)]
    public void WriteNs_RoundTripsThroughReader(uint value, uint n)
    {
        var writer = new Av1BitWriter();
        writer.WriteNs(value, n);
        writer.ByteAlign();

        var bytes = writer.ToArray();
        var reader = new Av1BitReader(bytes, 0, bytes.Length);
        Assert.Equal(value, reader.ReadNs(n));
    }

    [Fact]
    public void WriteNs_AllValuesInRange_RoundTrip()
    {
        for (uint n = 2; n <= 64; n++)
        {
            for (uint value = 0; value < n; value++)
            {
                var writer = new Av1BitWriter();
                writer.WriteNs(value, n);

                var bytes = writer.ToArray();
                var reader = new Av1BitReader(bytes, 0, bytes.Length);
                Assert.Equal(value, reader.ReadNs(n));
            }
        }
    }

    [Fact]
    public void ByteAlign_PadsToNextByteBoundary()
    {
        var writer = new Av1BitWriter();
        writer.WriteBits(1, 3);
        writer.ByteAlign();

        Assert.Equal(8, writer.BitsWritten);
    }

    [Fact]
    public void ToArray_GrowsBufferBeyondInitialCapacity()
    {
        var writer = new Av1BitWriter(initialByteCapacity: 1);
        for (int i = 0; i < 1000; i++)
        {
            writer.WriteFlag(i % 2 == 0);
        }

        var bytes = writer.ToArray();
        var reader = new Av1BitReader(bytes, 0, bytes.Length);
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(i % 2 == 0, reader.ReadFlag());
        }
    }

    [Fact]
    public void MixedFieldSequence_RoundTripsInOrder()
    {
        var writer = new Av1BitWriter();
        writer.WriteBits(5, 3);
        writer.WriteFlag(true);
        writer.WriteUvlc(42);
        writer.WriteSu(-5, 6);
        writer.WriteNs(3, 7);
        writer.ByteAlign();

        var bytes = writer.ToArray();
        var reader = new Av1BitReader(bytes, 0, bytes.Length);
        Assert.Equal(5u, reader.ReadBits(3));
        Assert.True(reader.ReadFlag());
        Assert.Equal(42u, reader.ReadUvlc());
        Assert.Equal(-5, reader.ReadSu(6));
        Assert.Equal(3u, reader.ReadNs(7));
    }
}
