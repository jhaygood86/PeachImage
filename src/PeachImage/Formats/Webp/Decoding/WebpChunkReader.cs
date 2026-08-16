using System.Buffers.Binary;

namespace PeachImage.Formats.Webp.Decoding;

/// <summary>One RIFF chunk's 4-character type code ("FourCC") and payload length, as read from a stream (payload/padding are handled separately).</summary>
internal readonly record struct WebpChunkHeader(string FourCc, uint Size);

/// <summary>Low-level RIFF chunk-framing stream primitives shared by <see cref="WebpContainerReader"/>.</summary>
internal static class WebpChunkReader
{
    private const int HeaderLength = 8;

    /// <summary>Reads the next chunk's FourCC + size, or returns <see langword="false"/> at a clean end of stream (no partial header bytes read).</summary>
    public static bool TryReadNext(Stream stream, out WebpChunkHeader header)
    {
        Span<byte> buffer = stackalloc byte[HeaderLength];
        int read = ReadUpTo(stream, buffer);
        if (read == 0)
        {
            header = default;
            return false;
        }

        if (read < HeaderLength)
        {
            throw new WebpDecodingException("Truncated WebP chunk header.");
        }

        string fourCc = System.Text.Encoding.ASCII.GetString(buffer[..4]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..8]);
        header = new WebpChunkHeader(fourCc, size);
        return true;
    }

    /// <summary>Reads a chunk's payload in full (including consuming the trailing pad byte if <paramref name="size"/> is odd).</summary>
    public static byte[] ReadPayload(Stream stream, uint size)
    {
        byte[] data = size == 0 ? [] : new byte[size];
        WebpStreamHelpers.ReadExactlyOrThrow(stream, data);
        SkipPadding(stream, size);
        return data;
    }

    /// <summary>Discards a chunk's payload without materializing it (including the trailing pad byte if <paramref name="size"/> is odd).</summary>
    public static void SkipPayload(Stream stream, uint size)
    {
        WebpStreamHelpers.SkipForward(stream, size);
        SkipPadding(stream, size);
    }

    private static void SkipPadding(Stream stream, uint size)
    {
        // RIFF chunks are padded to an even byte count; the pad byte's value is unspecified/ignored.
        if ((size & 1) != 0)
        {
            WebpStreamHelpers.SkipForward(stream, 1);
        }
    }

    private static int ReadUpTo(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
