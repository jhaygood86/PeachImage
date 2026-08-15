using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace PeachImage.Tests.Formats.Png;

/// <summary>Hand-assembles minimal, valid PNG byte streams for unit tests, independent of PeachImage's own encoder (so decoder tests aren't circular).</summary>
internal static class PngTestFileBuilder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <param name="scanlines">Each element is one already-filtered scanline: 1 filter-type byte followed by the filtered row bytes.</param>
    public static byte[] Build(int width, int height, byte bitDepth, byte colorType, byte[]? palette, byte[]? trns, IEnumerable<byte[]> scanlines, bool interlace = false)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..8], (uint)height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = interlace ? (byte)1 : (byte)0;
        WriteChunk(ms, "IHDR", ihdr);

        if (palette is not null)
        {
            WriteChunk(ms, "PLTE", palette);
        }

        if (trns is not null)
        {
            WriteChunk(ms, "tRNS", trns);
        }

        using var raw = new MemoryStream();
        foreach (var scanline in scanlines)
        {
            raw.Write(scanline);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(raw.ToArray());
        }

        WriteChunk(ms, "IDAT", compressed.ToArray());
        WriteChunk(ms, "IEND", ReadOnlySpan<byte>.Empty);

        return ms.ToArray();
    }

    public static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthAndType = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(lengthAndType[..4], (uint)data.Length);
        lengthAndType[4] = (byte)type[0];
        lengthAndType[5] = (byte)type[1];
        lengthAndType[6] = (byte)type[2];
        lengthAndType[7] = (byte)type[3];

        stream.Write(lengthAndType);
        stream.Write(data);

        var crc = new Crc32();
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
