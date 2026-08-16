namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>One tile's row/column position and byte range within the OBU payload, per spec §5.11.1 <c>tile_group_obu()</c>.</summary>
internal readonly record struct Av1TileRange(int TileRow, int TileCol, int Offset, int Length);

/// <summary>
/// Parses <c>tile_group_obu()</c> (spec §5.11.1): resolves every tile's byte range within the payload so
/// each can be handed to its own <see cref="Av1SymbolDecoder"/>. AV1's own internal tiling (this) is
/// orthogonal to AVIF's HEIF-level <c>grid</c> item tiling -- a single AVIF item's AV1 stream can itself
/// be split into multiple tiles by the encoder, independent of whether the AVIF file also composites
/// multiple items via a <c>grid</c>.
/// </summary>
internal static class Av1TileGroupObu
{
    public static List<Av1TileRange> Parse(byte[] data, int start, int length, Av1TileInfo tileInfo)
    {
        int numTiles = tileInfo.TileCols * tileInfo.TileRows;
        var reader = new Av1BitReader(data, start, length);

        bool tileStartAndEndPresent = false;
        if (numTiles > 1)
        {
            tileStartAndEndPresent = reader.ReadFlag();
        }

        int tgStart, tgEnd;
        if (numTiles == 1 || !tileStartAndEndPresent)
        {
            tgStart = 0;
            tgEnd = numTiles - 1;
        }
        else
        {
            int tileBits = tileInfo.TileColsLog2 + tileInfo.TileRowsLog2;
            tgStart = (int)reader.ReadBits(tileBits);
            tgEnd = (int)reader.ReadBits(tileBits);
        }

        reader.ByteAlign();
        int cursor = reader.BytePosition;
        int end = start + length;

        var result = new List<Av1TileRange>();
        for (int tileNum = tgStart; tileNum <= tgEnd; tileNum++)
        {
            int tileRow = tileNum / tileInfo.TileCols;
            int tileCol = tileNum % tileInfo.TileCols;
            bool lastTile = tileNum == tgEnd;

            int tileSize;
            if (lastTile)
            {
                tileSize = end - cursor;
            }
            else
            {
                if (cursor + tileInfo.TileSizeBytes > end)
                {
                    throw new AvifDecodingException("Truncated AV1 tile group: not enough bytes for a tile size field.");
                }

                long tileSizeMinus1 = (long)ReadLittleEndian(data, cursor, tileInfo.TileSizeBytes);
                tileSize = checked((int)(tileSizeMinus1 + 1));
                cursor += tileInfo.TileSizeBytes;
            }

            if (tileSize < 0 || cursor + tileSize > end)
            {
                throw new AvifDecodingException("AV1 tile size extends past the tile group's data.");
            }

            result.Add(new Av1TileRange(tileRow, tileCol, cursor, tileSize));
            cursor += tileSize;
        }

        return result;
    }

    /// <summary><c>le(n)</c> (spec §4.10.4): an n-byte little-endian unsigned integer, byte-aligned (used only for <c>tile_size_minus_1</c>, outside the bit reader's bit-level state).</summary>
    private static ulong ReadLittleEndian(byte[] data, int offset, int byteCount)
    {
        ulong value = 0;
        for (int i = 0; i < byteCount; i++)
        {
            value |= (ulong)data[offset + i] << (8 * i);
        }

        return value;
    }
}
