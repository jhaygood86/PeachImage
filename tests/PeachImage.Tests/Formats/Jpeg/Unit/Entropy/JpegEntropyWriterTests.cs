using PeachImage.Formats.Jpeg.Entropy;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Entropy;

public class JpegEntropyWriterTests
{
    [Fact]
    public void WriteBits_ThenFlush_ProducesExpectedBytes()
    {
        var stream = new MemoryStream();
        var writer = new JpegEntropyWriter(stream);

        writer.WriteBits(0b1010_1010, 8);
        writer.WriteBits(0b1111_0000, 8);
        writer.Flush();

        Assert.Equal(new byte[] { 0b1010_1010, 0b1111_0000 }, stream.ToArray());
    }

    [Fact]
    public void WriteBits_LiteralFF_IsByteStuffedWithZero()
    {
        var stream = new MemoryStream();
        var writer = new JpegEntropyWriter(stream);

        writer.WriteBits(0xFF, 8);
        writer.Flush();

        Assert.Equal(new byte[] { 0xFF, 0x00 }, stream.ToArray());
    }

    [Fact]
    public void WriteBits_AcrossInternalBufferBoundary_PreservesOrderAndStuffing()
    {
        var stream = new MemoryStream();
        var writer = new JpegEntropyWriter(stream);

        // The internal output buffer is 8192 bytes. Write enough bytes to force at least one
        // internal drain-to-stream mid-stream, including a stuffed 0xFF landing right at the
        // boundary, to verify AppendByte's buffer-full flush doesn't corrupt ordering.
        var expected = new List<byte>();
        for (int i = 0; i < 8190; i++)
        {
            byte b = (byte)(i & 0x7F); // never 0xFF, no stuffing noise for the bulk of the run
            writer.WriteBits(b, 8);
            expected.Add(b);
        }

        // These land at/after the buffer boundary and require stuffing.
        writer.WriteBits(0xFF, 8);
        expected.Add(0xFF);
        expected.Add(0x00);

        writer.WriteBits(0xFF, 8);
        expected.Add(0xFF);
        expected.Add(0x00);

        writer.WriteBits(0x42, 8);
        expected.Add(0x42);

        writer.Flush();

        Assert.Equal(expected, stream.ToArray());
    }

    [Fact]
    public void Flush_DrainsBufferedBytes_EvenWithNoPendingBits()
    {
        var stream = new MemoryStream();
        var writer = new JpegEntropyWriter(stream);

        // Two full bytes leave no partial bits pending, only buffered output bytes.
        writer.WriteBits(0x12, 8);
        writer.WriteBits(0x34, 8);

        // Nothing has been written to the underlying stream yet.
        Assert.Equal(0, stream.Length);

        writer.Flush();

        Assert.Equal(new byte[] { 0x12, 0x34 }, stream.ToArray());
    }

    [Fact]
    public void Reset_AfterFlush_ContinuesWritingCorrectlyForSubsequentData()
    {
        var stream = new MemoryStream();
        var writer = new JpegEntropyWriter(stream);

        writer.WriteBits(0xAB, 8);
        writer.Flush();
        writer.Reset();

        writer.WriteBits(0xCD, 8);
        writer.Flush();

        Assert.Equal(new byte[] { 0xAB, 0xCD }, stream.ToArray());
    }
}
