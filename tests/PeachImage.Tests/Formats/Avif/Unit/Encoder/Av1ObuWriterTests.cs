using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1ObuWriter"/> by round-tripping through the existing, already-correct
/// <see cref="Av1ObuReader"/>.
/// </summary>
public class Av1ObuWriterTests
{
    [Fact]
    public void WriteObu_EmptyPayload_RoundTrips()
    {
        var output = new List<byte>();
        Av1ObuWriter.WriteObu(output, Av1ObuType.TemporalDelimiter, ReadOnlySpan<byte>.Empty);

        var data = output.ToArray();
        var obus = Av1ObuReader.ReadObus(data, 0, data.Length);

        Assert.Single(obus);
        Assert.Equal(Av1ObuType.TemporalDelimiter, obus[0].Type);
        Assert.Equal(0, obus[0].PayloadLength);
    }

    [Fact]
    public void WriteObu_SmallPayload_RoundTrips()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        var output = new List<byte>();
        Av1ObuWriter.WriteObu(output, Av1ObuType.SequenceHeader, payload);

        var data = output.ToArray();
        var obus = Av1ObuReader.ReadObus(data, 0, data.Length);

        Assert.Single(obus);
        Assert.Equal(Av1ObuType.SequenceHeader, obus[0].Type);
        Assert.Equal(payload, data.AsSpan(obus[0].PayloadOffset, obus[0].PayloadLength).ToArray());
    }

    [Fact]
    public void WriteObu_LargePayload_RequiresMultiByteLeb128AndRoundTrips()
    {
        byte[] payload = new byte[300];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        var output = new List<byte>();
        Av1ObuWriter.WriteObu(output, Av1ObuType.TileGroup, payload);

        var data = output.ToArray();
        var obus = Av1ObuReader.ReadObus(data, 0, data.Length);

        Assert.Single(obus);
        Assert.Equal(Av1ObuType.TileGroup, obus[0].Type);
        Assert.Equal(payload, data.AsSpan(obus[0].PayloadOffset, obus[0].PayloadLength).ToArray());
    }

    [Fact]
    public void WriteObu_MultipleObus_RoundTripInOrder()
    {
        var output = new List<byte>();
        Av1ObuWriter.WriteObu(output, Av1ObuType.TemporalDelimiter, ReadOnlySpan<byte>.Empty);
        Av1ObuWriter.WriteObu(output, Av1ObuType.SequenceHeader, [0xAA, 0xBB]);
        Av1ObuWriter.WriteObu(output, Av1ObuType.FrameHeader, [0xCC]);
        Av1ObuWriter.WriteObu(output, Av1ObuType.TileGroup, [0x01, 0x02, 0x03]);

        var data = output.ToArray();
        var obus = Av1ObuReader.ReadObus(data, 0, data.Length);

        Assert.Equal(4, obus.Count);
        Assert.Equal(
            [Av1ObuType.TemporalDelimiter, Av1ObuType.SequenceHeader, Av1ObuType.FrameHeader, Av1ObuType.TileGroup],
            obus.Select(o => o.Type));
        Assert.Equal([0xAA, 0xBB], data.AsSpan(obus[1].PayloadOffset, obus[1].PayloadLength).ToArray());
        Assert.Equal([0x01, 0x02, 0x03], data.AsSpan(obus[3].PayloadOffset, obus[3].PayloadLength).ToArray());
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(127ul)]
    [InlineData(128ul)]
    [InlineData(16383ul)]
    [InlineData(16384ul)]
    [InlineData(1_000_000ul)]
    public void WriteLeb128_RoundTripsAsObuPayloadLength(ulong length)
    {
        // Drive leb128 encoding indirectly through WriteObu (the private ReadLeb128 in Av1ObuReader isn't
        // otherwise reachable) using a payload of exactly the target length.
        byte[] payload = new byte[length];
        var output = new List<byte>();
        Av1ObuWriter.WriteObu(output, Av1ObuType.Padding, payload);

        var data = output.ToArray();
        var obus = Av1ObuReader.ReadObus(data, 0, data.Length);

        Assert.Single(obus);
        Assert.Equal((int)length, obus[0].PayloadLength);
    }
}
