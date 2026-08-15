namespace PeachImage.Formats.Gif.Decoding;

/// <summary>Small stream-reading helpers shared across GIF decode internals.</summary>
internal static class GifStreamHelpers
{
    /// <summary>Reads exactly <paramref name="buffer"/>'s length of bytes, converting a short read into <see cref="GifDecodingException"/> rather than letting a raw I/O exception escape.</summary>
    public static void ReadExactlyOrThrow(Stream stream, Span<byte> buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            throw new GifDecodingException("Unexpected end of stream while reading GIF data.", ex);
        }
    }

    /// <summary>Reads a single byte, throwing <see cref="GifDecodingException"/> at end of stream instead of returning -1.</summary>
    public static byte ReadByteOrThrow(Stream stream)
    {
        int value = stream.ReadByte();
        if (value < 0)
        {
            throw new GifDecodingException("Unexpected end of stream while reading GIF data.");
        }

        return (byte)value;
    }

    /// <summary>Attempts to read a single byte. Returns <see langword="false"/> at end of stream instead of throwing.</summary>
    public static bool TryReadByte(Stream stream, out byte value)
    {
        int read = stream.ReadByte();
        if (read < 0)
        {
            value = 0;
            return false;
        }

        value = (byte)read;
        return true;
    }
}
