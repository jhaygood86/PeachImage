namespace PeachImage.Formats.Jpeg.Markers;

/// <summary>Reads marker codes and length-prefixed segments from a <see cref="JpegByteSource"/>.</summary>
internal sealed class JpegMarkerReader(JpegByteSource source)
{
    /// <summary>The underlying byte source, shared with the entropy-coded segment reader.</summary>
    public JpegByteSource Source => source;

    /// <summary>
    /// Scans forward for the next marker, skipping any non-marker fill bytes. Returns <see langword="null"/> at
    /// end of stream. A run of <c>0xFF</c> fill bytes before a marker code is skipped per spec allowance; a
    /// stuffed <c>0xFF 0x00</c> pair found here (outside entropy-coded data) is treated as stray and skipped.
    /// </summary>
    public JpegMarker? NextMarker()
    {
        while (true)
        {
            if (!source.TryReadByte(out byte b))
            {
                return null;
            }

            if (b != 0xFF)
            {
                continue;
            }

            byte code;
            do
            {
                if (!source.TryReadByte(out code))
                {
                    return null;
                }
            }
            while (code == 0xFF);

            if (code == 0x00)
            {
                continue;
            }

            return (JpegMarker)code;
        }
    }

    /// <summary>Reads a segment's 2-byte length field, returning the payload length (length field minus itself).</summary>
    public int ReadSegmentLength()
    {
        byte hi = source.ReadByteOrThrow();
        byte lo = source.ReadByteOrThrow();
        int length = (hi << 8) | lo;
        if (length < 2)
        {
            throw new JpegDecodingException($"Invalid JPEG segment length {length}.");
        }

        return length - 2;
    }

    /// <summary>Reads exactly <paramref name="destination"/>'s length bytes of segment payload.</summary>
    public void ReadSegmentBytes(Span<byte> destination)
    {
        int read = source.ReadSegmentPayload(destination);
        if (read != destination.Length)
        {
            throw new JpegDecodingException("Unexpected end of stream while reading a JPEG segment.");
        }
    }

    /// <summary>Skips a segment's payload without reading it.</summary>
    public void SkipSegment(int payloadLength) => source.SkipBytes(payloadLength);
}
