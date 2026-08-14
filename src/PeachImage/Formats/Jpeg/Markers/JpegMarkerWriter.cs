namespace PeachImage.Formats.Jpeg.Markers;

/// <summary>Writes marker codes and length-prefixed segments to a <see cref="Stream"/> during encoding.</summary>
internal static class JpegMarkerWriter
{
    /// <summary>Writes a bare marker (no length/payload), e.g. SOI or EOI.</summary>
    public static void WriteMarkerOnly(Stream stream, JpegMarker marker)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte((byte)marker);
    }

    /// <summary>Writes a length-prefixed segment.</summary>
    public static void WriteSegment(Stream stream, JpegMarker marker, ReadOnlySpan<byte> payload)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte((byte)marker);

        int length = payload.Length + 2;
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)(length & 0xFF));
        stream.Write(payload);
    }
}
