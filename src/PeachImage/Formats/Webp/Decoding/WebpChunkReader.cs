using PeachImage.Formats.Shared.IO;
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
        if (!BoundedPayloadReader.TryRead(stream, size, out byte[] data))
        {
            throw new WebpDecodingException("Truncated WebP chunk.");
        }

        SkipPadding(stream, size);
        return data;
    }

    /// <summary>
    /// Reads a chunk's payload into a buffer rented from <see cref="Internal.WebpBufferPool"/> (so longer than <paramref name="size"/>
    /// is possible), or returns <see langword="null"/> without reading when the stream cannot vouch for the size up front: a stream that
    /// is not seekable, or one holding fewer bytes than the chunk claims, is left to <see cref="ReadPayload"/>, which will not trust the claim.
    /// </summary>
    public static byte[]? TryReadPayloadPooled(Stream stream, uint size)
    {
        if (size == 0 || !stream.CanSeek || size > stream.Length - stream.Position || size > int.MaxValue)
        {
            return null;
        }

        byte[] buffer = Internal.WebpBufferPool.Shared.Rent((int)size);
        try
        {
            if (stream.ReadAtLeast(buffer.AsSpan(0, (int)size), (int)size, throwOnEndOfStream: false) != (int)size)
            {
                throw new WebpDecodingException("Truncated WebP chunk.");
            }

            SkipPadding(stream, size);
            return buffer;
        }
        catch
        {
            Internal.WebpBufferPool.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>Reads only the first <paramref name="prefixLength"/> bytes of a chunk's payload (all of it when shorter) and skips the rest, including the pad byte.</summary>
    public static byte[] ReadPrefix(Stream stream, uint size, int prefixLength)
    {
        int take = (int)Math.Min(size, (uint)prefixLength);
        byte[] data = new byte[take];
        if (take > 0 && stream.ReadAtLeast(data, take, throwOnEndOfStream: false) != take)
        {
            throw new WebpDecodingException("Truncated WebP chunk.");
        }

        WebpStreamHelpers.SkipForward(stream, size - (uint)take);
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
