using System.Buffers.Binary;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Decoding;

/// <summary>One chunk's length + type, as read from a stream (data/CRC are handled separately).</summary>
internal readonly record struct PngChunkHeader(uint Length, PngChunkType Type);

/// <summary>Low-level chunk-framing stream primitives shared by every chunk reader.</summary>
internal static class PngChunkReader
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Reads and validates the 8-byte PNG signature.</summary>
    public static void ReadSignature(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadExactlyOrThrow(stream, buffer);
        if (!buffer.SequenceEqual(Signature))
        {
            throw new PngDecodingException("Stream does not start with the PNG signature.");
        }
    }

    /// <summary>Reads a chunk's 4-byte length and 4-byte type code.</summary>
    public static PngChunkHeader ReadHeader(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadExactlyOrThrow(stream, buffer);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
        if (length > PngDecodingLimits.MaxChunkDataLength)
        {
            throw new PngDecodingException($"PNG chunk declares an implausible length of {length} bytes.");
        }

        return new PngChunkHeader(length, PngChunkType.FromAscii(buffer[4..8]));
    }

    /// <summary>Reads a chunk's data payload and validates its trailing CRC32 against the type+data bytes.</summary>
    public static byte[] ReadDataAndValidateCrc(Stream stream, PngChunkHeader header)
    {
        byte[] data = header.Length == 0 ? [] : new byte[header.Length];
        ReadExactlyOrThrow(stream, data);
        ValidateCrc(stream, header.Type, data);
        return data;
    }

    /// <summary>Discards a chunk's data payload and trailing CRC without validating it (used for skipped unknown ancillary chunks).</summary>
    public static void SkipChunk(Stream stream, PngChunkHeader header)
    {
        PngStreamHelpers.SkipForward(stream, header.Length + 4);
    }

    private static void ValidateCrc(Stream stream, PngChunkType type, ReadOnlySpan<byte> data)
    {
        Span<byte> typeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(typeBytes, type.Value);

        var crc = new System.IO.Hashing.Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> computedBytes = stackalloc byte[4];
        crc.GetCurrentHash(computedBytes);

        // System.IO.Hashing.Crc32 writes its hash little-endian; PNG's on-disk CRC field is big-endian.
        uint computed = BinaryPrimitives.ReadUInt32LittleEndian(computedBytes);

        Span<byte> stored = stackalloc byte[4];
        ReadExactlyOrThrow(stream, stored);
        uint storedValue = BinaryPrimitives.ReadUInt32BigEndian(stored);

        if (computed != storedValue)
        {
            throw new PngDecodingException($"CRC mismatch in '{type}' chunk.");
        }
    }

    /// <summary>Shared by <see cref="PngIdatStream"/> too, for CRC-trailer/next-header reads that need the same "wrap I/O failures" behavior.</summary>
    internal static void ReadExactlyOrThrow(Stream stream, Span<byte> buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            throw new PngDecodingException("Unexpected end of stream while reading PNG data.", ex);
        }
    }
}
