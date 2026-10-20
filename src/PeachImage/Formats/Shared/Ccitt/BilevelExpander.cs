using System.Buffers.Binary;

namespace PeachImage.Formats.Shared.Ccitt;

/// <summary>Expands packed one-bit rows into one byte per pixel (<c>0x00</c> or <c>0xFF</c>) with a byte-to-eight-pixels lookup.</summary>
internal static class BilevelExpander
{
    // Entry i holds the eight pixels of byte i, first pixel in the lowest byte: a 1 bit becomes 0xFF.
    private static readonly ulong[] Table = BuildTable();

    /// <summary>Expands one packed row. A set bit becomes 255 (or 0 when <paramref name="invert"/> is true).</summary>
    public static void ExpandRow(ReadOnlySpan<byte> packed, Span<byte> destination, int width, bool invert)
    {
        ulong flip = invert ? ulong.MaxValue : 0;
        int fullBytes = width >> 3;
        for (int i = 0; i < fullBytes; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(i << 3, 8), Table[packed[i]] ^ flip);
        }

        int remainder = width & 7;
        if (remainder != 0)
        {
            ulong last = Table[packed[fullBytes]] ^ flip;
            for (int p = 0; p < remainder; p++)
            {
                destination[(fullBytes << 3) + p] = (byte)(last >> (p << 3));
            }
        }
    }

    private static ulong[] BuildTable()
    {
        var table = new ulong[256];
        for (int value = 0; value < 256; value++)
        {
            ulong entry = 0;
            for (int p = 0; p < 8; p++)
            {
                if ((value & (0x80 >> p)) != 0)
                {
                    entry |= 0xFFUL << (p << 3);
                }
            }

            table[value] = entry;
        }

        return table;
    }
}
