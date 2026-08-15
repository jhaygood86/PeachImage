using System.Buffers.Binary;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Encoding;

/// <summary>Writes a single length-prefixed, CRC32-checked PNG chunk.</summary>
internal static class PngChunkWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void WriteSignature(Stream stream) => stream.Write(Signature);

    public static void WriteChunk(Stream stream, PngChunkType type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthAndType = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(lengthAndType[..4], (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(lengthAndType[4..], type.Value);
        stream.Write(lengthAndType);
        stream.Write(data);

        var crc = new System.IO.Hashing.Crc32();
        crc.Append(lengthAndType[4..]);
        crc.Append(data);
        Span<byte> crcBytes = stackalloc byte[4];
        crc.GetCurrentHash(crcBytes);

        // System.IO.Hashing.Crc32 writes its hash little-endian; PNG's on-disk CRC field is big-endian.
        Span<byte> crcOnDisk = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcOnDisk, BinaryPrimitives.ReadUInt32LittleEndian(crcBytes));
        stream.Write(crcOnDisk);
    }
}
