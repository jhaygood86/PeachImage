using System.Buffers.Binary;

namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// A bounds-checked, byte-order-aware random-access reader over an already-buffered TIFF file. TIFF's IFD
/// entries, strip offsets, and palette all address the file by absolute offset and jump around arbitrarily
/// (there is no sequential-stream story that avoids buffering the whole file first — see
/// <see cref="TiffStreamHelpers.BufferStream"/>, which mirrors Avif's <c>AvifContainerReader</c> for the
/// same reason), so every read here operates directly on the in-memory buffer rather than a <see cref="Stream"/>.
/// Every read bounds-checks its own offset/length against the buffer, throwing <see cref="TiffDecodingException"/>
/// rather than letting a malformed/hostile offset overrun into an <see cref="IndexOutOfRangeException"/> or
/// silently read garbage.
/// </summary>
internal readonly struct TiffReader(byte[] data, TiffByteOrder byteOrder)
{
    /// <summary>The buffered file contents.</summary>
    public byte[] Data { get; } = data;

    /// <summary>The byte order every multi-byte read below honors.</summary>
    public TiffByteOrder ByteOrder { get; } = byteOrder;

    /// <summary>The buffered file's total length, in bytes.</summary>
    public int Length => Data.Length;

    /// <summary>Whether <paramref name="count"/> bytes starting at <paramref name="offset"/> lie entirely within the buffer.</summary>
    public bool HasBytes(int offset, int count) => offset >= 0 && count >= 0 && (long)offset + count <= Data.Length;

    public byte ReadByte(int offset)
    {
        EnsureInBounds(offset, 1);
        return Data[offset];
    }

    public ushort ReadUInt16(int offset)
    {
        EnsureInBounds(offset, 2);
        var span = Data.AsSpan(offset, 2);
        return ByteOrder == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(span)
            : BinaryPrimitives.ReadUInt16BigEndian(span);
    }

    public uint ReadUInt32(int offset)
    {
        EnsureInBounds(offset, 4);
        var span = Data.AsSpan(offset, 4);
        return ByteOrder == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(span)
            : BinaryPrimitives.ReadUInt32BigEndian(span);
    }

    /// <summary>Reads a raw byte span, for callers that decompress/copy a run of bytes (strip data, palette bytes) rather than parsing a typed field.</summary>
    public ReadOnlySpan<byte> ReadSpan(int offset, int count)
    {
        EnsureInBounds(offset, count);
        return Data.AsSpan(offset, count);
    }

    private void EnsureInBounds(int offset, int count)
    {
        if (!HasBytes(offset, count))
        {
            throw new TiffDecodingException($"Attempted to read {count} byte(s) at offset {offset}, past the end of a {Data.Length}-byte TIFF file.");
        }
    }
}
