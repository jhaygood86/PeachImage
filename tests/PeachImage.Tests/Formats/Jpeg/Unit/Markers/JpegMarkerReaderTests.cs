using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jpeg.Markers;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Markers;

public class JpegMarkerReaderTests
{
    [Fact]
    public void NextMarker_SkipsFillBytesBeforeMarkerCode()
    {
        // A run of extra 0xFF fill bytes before the real marker code is explicitly allowed by the spec.
        var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0xD8]);
        var reader = new JpegMarkerReader(new JpegByteSource(stream));

        Assert.Equal(JpegMarker.Soi, reader.NextMarker());
    }

    [Fact]
    public void NextMarker_SkipsStrayStuffedBytesOutsideEntropyData()
    {
        var stream = new MemoryStream([0xFF, 0x00, 0xFF, 0xD8]);
        var reader = new JpegMarkerReader(new JpegByteSource(stream));

        Assert.Equal(JpegMarker.Soi, reader.NextMarker());
    }

    [Fact]
    public void NextMarker_ReturnsNull_AtEndOfStream()
    {
        var stream = new MemoryStream([]);
        var reader = new JpegMarkerReader(new JpegByteSource(stream));

        Assert.Null(reader.NextMarker());
    }

    [Fact]
    public void ReadSegmentLength_ReturnsPayloadLengthExcludingTheLengthFieldItself()
    {
        // Length field value 6 means 6 total bytes including itself, i.e. 4 bytes of payload.
        var stream = new MemoryStream([0x00, 0x06, 0xAA, 0xBB, 0xCC, 0xDD]);
        var reader = new JpegMarkerReader(new JpegByteSource(stream));

        int payloadLength = reader.ReadSegmentLength();

        Assert.Equal(4, payloadLength);
        Span<byte> payload = stackalloc byte[4];
        reader.ReadSegmentBytes(payload);
        Assert.Equal([0xAA, 0xBB, 0xCC, 0xDD], payload.ToArray());
    }

    [Fact]
    public void ReadSegmentLength_OfInvalidLength_ThrowsJpegDecodingException()
    {
        var stream = new MemoryStream([0x00, 0x01]); // length < 2 is invalid: the field must count itself
        var reader = new JpegMarkerReader(new JpegByteSource(stream));

        Assert.Throws<JpegDecodingException>(() => reader.ReadSegmentLength());
    }

    [Fact]
    public void Decode_TruncatedStream_MissingSoi_ThrowsJpegDecodingException()
    {
        // Bytes that never start with FFD8 at all: FrameDecoder should reject immediately, not hang.
        using var stream = new MemoryStream([0x00, 0x01, 0x02, 0x03]);
        Assert.Throws<JpegDecodingException>(() => JpegDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_TruncatedStream_MissingEoi_ThrowsJpegDecodingException()
    {
        // Just SOI, nothing else: should fail cleanly with "missing EOI", not throw an unrelated exception or hang.
        using var stream = new MemoryStream([0xFF, 0xD8]);
        Assert.Throws<JpegDecodingException>(() => JpegDecoder.Decode(stream));
    }
}
