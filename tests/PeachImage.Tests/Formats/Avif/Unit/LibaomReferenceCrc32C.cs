namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Independent transcription of libaom's own real <c>av1_get_crc32c_value_c</c>/<c>av1_crc32c_calculator_init</c>
/// (<c>av1/encoder/hash.c</c>) -- the real slice-by-8 CRC-32C (iSCSI polynomial, reversed bit order)
/// software fallback IntraBC's own real block-hash matcher uses. Deliberately transcribed as the full
/// 8-tables-deep sliced algorithm (not the simpler byte-at-a-time loop
/// <see cref="PeachImage.Formats.Avif.Encoder.Av1.Av1IntrabcCrc32C.Compute"/> already uses) so this is a
/// genuinely different implementation shape to check against, not a restatement of the same simple loop --
/// both are mathematically the same CRC-32C recurrence, but arrived at independently here.
/// </summary>
internal static class LibaomReferenceCrc32C
{
    private static readonly uint[][] Table = BuildTables();

    private static uint[][] BuildTables()
    {
        const uint poly = 0x82f63b78u;
        var table = new uint[8][];
        for (int k = 0; k < 8; k++)
        {
            table[k] = new uint[256];
        }

        for (uint n = 0; n < 256; n++)
        {
            uint crc = n;
            for (int b = 0; b < 8; b++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }

            table[0][n] = crc;
        }

        for (uint n = 0; n < 256; n++)
        {
            uint crc = table[0][n];
            for (int k = 1; k < 8; k++)
            {
                crc = table[0][crc & 0xff] ^ (crc >> 8);
                table[k][n] = crc;
            }
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> buf)
    {
        int next = 0;
        int len = buf.Length;
        ulong crc = 0 ^ 0xffffffffu;

        // libaom's own real 8-byte-alignment prologue (aligns `next` to an 8-byte boundary before the
        // sliced main loop) -- this port has no real pointer to align, but reproduces the exact same byte
        // count processed by each phase so the arithmetic sequence (and thus the result) matches exactly.
        while (len != 0 && (next & 7) != 0)
        {
            crc = Table[0][(crc ^ buf[next++]) & 0xff] ^ (crc >> 8);
            len--;
        }

        while (len >= 8)
        {
            ulong chunk = 0;
            for (int i = 0; i < 8; i++)
            {
                chunk |= (ulong)buf[next + i] << (8 * i);
            }

            crc ^= chunk;
            crc = Table[7][crc & 0xff] ^ Table[6][(crc >> 8) & 0xff] ^
                  Table[5][(crc >> 16) & 0xff] ^ Table[4][(crc >> 24) & 0xff] ^
                  Table[3][(crc >> 32) & 0xff] ^ Table[2][(crc >> 40) & 0xff] ^
                  Table[1][(crc >> 48) & 0xff] ^ Table[0][crc >> 56];
            next += 8;
            len -= 8;
        }

        while (len != 0)
        {
            crc = Table[0][(crc ^ buf[next++]) & 0xff] ^ (crc >> 8);
            len--;
        }

        return (uint)crc ^ 0xffffffffu;
    }
}
