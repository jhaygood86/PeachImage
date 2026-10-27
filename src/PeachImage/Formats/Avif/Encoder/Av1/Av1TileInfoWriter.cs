using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Writes <c>tile_info()</c> (spec §5.9.15) -- the write-side mirror of <see cref="Av1TileInfo"/>,
/// restricted to one tile column and a uniformly spaced set of tile rows (a single tile row by default). Single
/// tile is only architecturally guaranteed when <c>min_log2_tile_cols</c>/<c>min_log2_tile_rows</c> (per
/// spec's own tile-size constraints, mirrored here from <see cref="Av1TileInfo.Parse"/>'s math) both work
/// out to 0, which holds for images up to roughly 4096 (64x64-superblock) or 8192 (128x128-superblock,
/// lossless) pixels per side -- the top-level image encoder (added alongside the container-writer/
/// orchestration layer) enforces a dimension cap consistent with that before this is ever called.
/// </summary>
internal static class Av1TileInfoWriter
{
    private const int MaxTileWidth = 4096;
    private const int MaxTileArea = 4096 * 2304;

    /// <summary>
    /// The superblock-row-aligned tile row starts (in mi units, plus the final <paramref name="miRows"/>
    /// sentinel) spec's uniform tile spacing gives for <paramref name="tileRowsLog2"/>: tiles are
    /// <c>ceil(sbRows / 2^tileRowsLog2)</c> superblock rows tall, so there can be fewer than <c>2^tileRowsLog2</c> of them.
    /// </summary>
    public static int[] ComputeRowStarts(int miRows, bool use128x128Superblock, int tileRowsLog2)
    {
        int sbShift = use128x128Superblock ? 5 : 4;
        int sbRows = (miRows + (1 << sbShift) - 1) >> sbShift;
        int tileHeightSb = (sbRows + (1 << tileRowsLog2) - 1) >> tileRowsLog2;
        var starts = new List<int>();
        for (int startSb = 0; startSb < sbRows; startSb += tileHeightSb)
        {
            starts.Add(startSb << sbShift);
        }

        starts.Add(miRows);
        return [.. starts];
    }

    /// <summary>The largest <c>tile_rows_log2</c> a frame of <paramref name="miRows"/> can signal (spec's <c>maxLog2TileRows</c>).</summary>
    public static int MaxTileRowsLog2(int miRows, bool use128x128Superblock)
    {
        int sbShift = use128x128Superblock ? 5 : 4;
        int sbRows = (miRows + (1 << sbShift) - 1) >> sbShift;
        return TileLog2(1, Math.Min(sbRows, 64));
    }

    /// <summary>
    /// Writes <c>tile_info()</c> for a full-width (single tile column) frame split into <paramref name="tileRowsLog2"/>-spaced
    /// tile rows (<paramref name="miCols"/> x <paramref name="miRows"/> in 4x4 mode-info units; <paramref name="use128x128Superblock"/> must agree with the sequence header). <paramref name="tileSizeBytes"/> (1-4) is the width of each non-final tile's size field in the tile group; it is only
    /// signaled when there is more than one tile.
    /// </summary>
    public static Av1TileInfo Write(Av1BitWriter writer, int miCols, int miRows, bool use128x128Superblock = false, int tileRowsLog2 = 0, int tileSizeBytes = 1)
    {
        int sbShift = use128x128Superblock ? 5 : 4;
        int sbSize = sbShift + 2;
        int sbCols = (miCols + (1 << sbShift) - 1) >> sbShift;
        int sbRows = (miRows + (1 << sbShift) - 1) >> sbShift;

        int maxTileWidthSb = MaxTileWidth >> sbSize;
        int maxTileAreaSb = MaxTileArea >> (2 * sbSize);
        int minLog2TileCols = TileLog2(maxTileWidthSb, sbCols);
        int maxLog2TileCols = TileLog2(1, Math.Min(sbCols, 64));
        int maxLog2TileRows = TileLog2(1, Math.Min(sbRows, 64));
        int minLog2Tiles = Math.Max(minLog2TileCols, TileLog2(maxTileAreaSb, sbRows * sbCols));

        if (minLog2TileCols > 0)
        {
            int sbPixels = 1 << sbSize;
            throw new AvifEncodingException($"Image is too wide for this encoder's single-tile AV1 configuration ({sbCols} {sbPixels}x{sbPixels} superblock columns; must fit within {maxTileWidthSb}).");
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
            int sbPixels = 1 << sbSize;
            throw new AvifEncodingException($"Image is too tall for this encoder's single-tile AV1 configuration ({sbRows} {sbPixels}x{sbPixels} superblock rows).");
        }

        if (tileRowsLog2 < minLog2TileRows || tileRowsLog2 > maxLog2TileRows)
        {
            throw new ArgumentOutOfRangeException(nameof(tileRowsLog2), tileRowsLog2, $"tile_rows_log2 must be within {minLog2TileRows}..{maxLog2TileRows} for this frame size.");
        }

        // tile_rows_log2: one increment flag per step up, then a terminating 0 unless already at the maximum.
        for (int i = minLog2TileRows; i < tileRowsLog2; i++)
        {
            writer.WriteFlag(true);
        }

        if (tileRowsLog2 < maxLog2TileRows)
        {
            writer.WriteFlag(false);
        }

        int[] rowStarts = ComputeRowStarts(miRows, use128x128Superblock, tileRowsLog2);
        if (tileRowsLog2 > 0)
        {
            // context_update_tile_id (which tile's final CDFs a following frame would inherit -- irrelevant for a
            // still image, tile 0) and tile_size_bytes_minus_1.
            writer.WriteBits(0, tileRowsLog2);
            writer.WriteBits((uint)(tileSizeBytes - 1), 2);
        }

        return new Av1TileInfo
        {
            TileCols = 1,
            TileRows = rowStarts.Length - 1,
            TileColsLog2 = 0,
            TileRowsLog2 = tileRowsLog2,
            MiColStarts = [0, miCols],
            MiRowStarts = rowStarts,
            ContextUpdateTileId = 0,
            TileSizeBytes = tileRowsLog2 > 0 ? tileSizeBytes : 1,
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
