using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Writes <c>tile_info()</c> (spec §5.9.15) for the always-single-tile v1 encoder -- the write-side mirror
/// of <see cref="Av1TileInfo"/>, restricted to producing exactly one tile column and one tile row. Single
/// tile is only architecturally guaranteed when <c>min_log2_tile_cols</c>/<c>min_log2_tile_rows</c> (per
/// spec's own tile-size constraints, mirrored here from <see cref="Av1TileInfo.Parse"/>'s math) both work
/// out to 0, which holds for images up to roughly 4096 pixels per side at the fixed 64x64-superblock,
/// non-128x128 configuration this encoder always uses -- the top-level image encoder (added alongside the
/// container-writer/orchestration layer) enforces a dimension cap consistent with that before this is ever
/// called.
/// </summary>
internal static class Av1TileInfoWriter
{
    private const int MaxTileWidth = 4096;
    private const int MaxTileArea = 4096 * 2304;

    /// <summary>Writes a single-tile <c>tile_info()</c> for a <paramref name="miCols"/> x <paramref name="miRows"/> frame (in 4x4 mode-info units) and returns its resolved <see cref="Av1TileInfo"/>.</summary>
    public static Av1TileInfo Write(Av1BitWriter writer, int miCols, int miRows)
    {
        // use_128x128_superblock is always false for this encoder (Av1SequenceHeaderWriter).
        const int sbShift = 4;
        const int sbSize = sbShift + 2;
        int sbCols = (miCols + 15) >> 4;
        int sbRows = (miRows + 15) >> 4;

        int maxTileWidthSb = MaxTileWidth >> sbSize;
        int maxTileAreaSb = MaxTileArea >> (2 * sbSize);
        int minLog2TileCols = TileLog2(maxTileWidthSb, sbCols);
        int maxLog2TileCols = TileLog2(1, Math.Min(sbCols, 64));
        int maxLog2TileRows = TileLog2(1, Math.Min(sbRows, 64));
        int minLog2Tiles = Math.Max(minLog2TileCols, TileLog2(maxTileAreaSb, sbRows * sbCols));

        if (minLog2TileCols > 0)
        {
            throw new AvifEncodingException($"Image is too wide for this encoder's single-tile AV1 configuration ({sbCols} 64x64 superblock columns; must fit within {maxTileWidthSb}).");
        }

        writer.WriteFlag(true); // uniform_tile_spacing_flag

        // tile_cols_log2: stop growing at the very first opportunity so tile_cols == 1.
        int tileColsLog2 = 0;
        if (tileColsLog2 < maxLog2TileCols)
        {
            writer.WriteFlag(false);
        }

        int minLog2TileRows = Math.Max(minLog2Tiles - tileColsLog2, 0);
        if (minLog2TileRows > 0)
        {
            throw new AvifEncodingException($"Image is too tall for this encoder's single-tile AV1 configuration ({sbRows} 64x64 superblock rows).");
        }

        int tileRowsLog2 = 0;
        if (tileRowsLog2 < maxLog2TileRows)
        {
            writer.WriteFlag(false);
        }

        // tile_cols_log2 == tile_rows_log2 == 0 -> context_update_tile_id/tile_size_bytes_minus_1 are not read.
        return new Av1TileInfo
        {
            TileCols = 1,
            TileRows = 1,
            TileColsLog2 = 0,
            TileRowsLog2 = 0,
            MiColStarts = [0, miCols],
            MiRowStarts = [0, miRows],
            ContextUpdateTileId = 0,
            TileSizeBytes = 1,
        };
    }

    private static int TileLog2(int blkSize, int target)
    {
        int k = 0;
        while ((blkSize << k) < target)
        {
            k++;
        }

        return k;
    }
}
