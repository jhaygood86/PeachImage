using System.Buffers.Binary;
using System.Text;

namespace PeachImage.Formats.Avif.Container;

/// <summary>Big-endian binary primitives shared by every ISOBMFF box parser under <c>Formats.Avif.Container</c>. ISOBMFF is big-endian throughout, unlike WebP's little-endian RIFF.</summary>
internal static class AvifBinaryReader
{
    public static string ReadFourCc(byte[] data, int offset) => Encoding.ASCII.GetString(data, offset, 4);

    public static byte ReadUInt8(byte[] data, ref int offset)
    {
        byte value = data[offset];
        offset += 1;
        return value;
    }

    public static ushort ReadUInt16(byte[] data, ref int offset)
    {
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
        offset += 2;
        return value;
    }

    public static uint ReadUInt32(byte[] data, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    public static ulong ReadUInt64(byte[] data, ref int offset)
    {
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));
        offset += 8;
        return value;
    }

    /// <summary>Reads a big-endian unsigned integer occupying exactly <paramref name="byteCount"/> bytes (0-8), advancing <paramref name="offset"/>. Used for ISOBMFF's variable-width fields (<c>iloc</c>'s offset/length/base_offset/index sizes).</summary>
    public static ulong ReadUIntN(byte[] data, ref int offset, int byteCount)
    {
        if (byteCount == 0)
        {
            return 0;
        }

        ulong value = 0;
        for (int i = 0; i < byteCount; i++)
        {
            value = (value << 8) | data[offset + i];
        }

        offset += byteCount;
        return value;
    }

    /// <summary>Reads a NUL-terminated UTF-8 string, stopping at <paramref name="end"/> if no terminator is found before it (tolerated rather than treated as truncation — some encoders omit the trailing NUL on the last string in a box).</summary>
    public static string ReadCString(byte[] data, ref int offset, int end)
    {
        int start = offset;
        int i = start;
        while (i < end && data[i] != 0)
        {
            i++;
        }

        string value = Encoding.UTF8.GetString(data, start, i - start);
        offset = i < end ? i + 1 : i;
        return value;
    }
}
