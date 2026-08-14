using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Jpeg.Markers;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Entropy;

public class JpegEntropyReaderTests
{
    [Fact]
    public void GetBits_DestuffsFF00AsLiteralFF()
    {
        // 0xFF 0x00 is byte-stuffed data representing a literal 0xFF data byte.
        var stream = new MemoryStream([0xFF, 0x00]);
        var reader = new JpegEntropyReader(new JpegByteSource(stream));

        int value = reader.GetBits(8);

        Assert.Equal(0xFF, value);
        Assert.False(reader.AtMarkerBoundary);
    }

    [Fact]
    public void GetBits_StopsAtRealMarker_AndReportsMarkerBoundary()
    {
        // 0xFF 0xD9 is a real marker (EOI), not stuffed data: the reader should treat this as end of
        // entropy data (padding with zero bits) rather than consuming it as data.
        var stream = new MemoryStream([0x00, 0xFF, 0xD9]);
        var reader = new JpegEntropyReader(new JpegByteSource(stream));

        int firstByte = reader.GetBits(8);
        Assert.Equal(0x00, firstByte);

        // Further reads should return zero-padded bits and flag the marker boundary.
        reader.GetBits(8);
        Assert.True(reader.AtMarkerBoundary);
    }

    [Fact]
    public void ReceiveExtend_MatchesSpecExtendProcedure()
    {
        var stream = new MemoryStream([0b1010_0000]); // top 3 bits: 101 = 5
        var reader = new JpegEntropyReader(new JpegByteSource(stream));

        // magnitude=3 bits, raw value 5 (0b101) >= threshold(4) => positive, value stays 5.
        int value = reader.ReceiveExtend(3);
        Assert.Equal(5, value);
    }

    [Fact]
    public void ReceiveExtend_OfZeroMagnitude_ReturnsZero()
    {
        var stream = new MemoryStream([0x00]);
        var reader = new JpegEntropyReader(new JpegByteSource(stream));

        Assert.Equal(0, reader.ReceiveExtend(0));
    }

    [Fact]
    public void ReceiveExtend_BelowThreshold_ProducesNegativeValue()
    {
        var stream = new MemoryStream([0b0100_0000]); // top 3 bits: 010 = 2, below threshold 4 for size 3
        var reader = new JpegEntropyReader(new JpegByteSource(stream));

        int value = reader.ReceiveExtend(3);

        // Extend: 2 < 4 (threshold) => value = 2 + (-(1<<3)+1) = 2 - 7 = -5.
        Assert.Equal(-5, value);
    }

    [Fact]
    public void ConsumeRestartMarker_SucceedsOnMatchingMarker_AndThrowsOnMismatch()
    {
        var matching = new MemoryStream([0xFF, 0xD3]); // RST3
        var readerOk = new JpegEntropyReader(new JpegByteSource(matching));
        readerOk.AlignAndDiscardPartialByte();
        readerOk.ConsumeRestartMarker(3); // should not throw

        var mismatched = new MemoryStream([0xFF, 0xD5]); // RST5, but we expect RST3
        var readerBad = new JpegEntropyReader(new JpegByteSource(mismatched));
        readerBad.AlignAndDiscardPartialByte();
        Assert.Throws<JpegDecodingException>(() => readerBad.ConsumeRestartMarker(3));
    }
}
