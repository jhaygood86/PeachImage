namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// Decodes an item's color (or alpha) tile byte streams and composites them into one set of planes
/// cropped to the item's declared output size. For a non-grid item this is just "decode the one tile and
/// crop away its superblock-aligned padding"; for a HEIF <c>grid</c> item, every tile is independently
/// decoded (each tile is its own complete AV1 keyframe) and placed at its row/column position, with the
/// last row/column of tiles cropped where they overhang the grid's declared output size -- ordinary HEIF
/// grid semantics, not an AVIF-specific quirk.
/// </summary>
internal static class Av1TileComposer
{
    public sealed class CompositedFrame
    {
        public required int[][] Planes { get; init; }

        public required int[] Widths { get; init; }

        public required int[] Heights { get; init; }

        public required Av1SequenceHeader Sequence { get; init; }
    }

    public static CompositedFrame Composite(IReadOnlyList<byte[]> tiles, int gridRows, int gridColumns, int outWidth, int outHeight)
    {
        var frames = new Av1FrameDecodeResult[tiles.Count];
        for (int i = 0; i < tiles.Count; i++)
        {
            frames[i] = Av1FrameDecoder.Decode(tiles[i]);
        }

        var seq = frames[0].Sequence;
        int numPlanes = seq.MonoChrome ? 1 : 3;

        var outPlanes = new int[3][];
        var outWidths = new int[3];
        var outHeights = new int[3];
        for (int plane = 0; plane < numPlanes; plane++)
        {
            int subX = plane > 0 && seq.SubsamplingX ? 1 : 0;
            int subY = plane > 0 && seq.SubsamplingY ? 1 : 0;
            outWidths[plane] = (outWidth + subX) >> subX;
            outHeights[plane] = (outHeight + subY) >> subY;
            outPlanes[plane] = new int[outWidths[plane] * outHeights[plane]];
        }

        if (gridRows == 1 && gridColumns == 1)
        {
            var single = frames[0];
            for (int plane = 0; plane < numPlanes; plane++)
            {
                int copyW = Math.Min(outWidths[plane], single.PlaneWidths[plane]);
                int copyH = Math.Min(outHeights[plane], single.PlaneHeights[plane]);
                CopyRegion(single.Planes[plane], single.PlaneWidths[plane], outPlanes[plane], outWidths[plane], 0, 0, copyW, copyH);
            }
        }
        else
        {
            int tileContentW = frames[0].Frame.UpscaledWidth;
            int tileContentH = frames[0].Frame.FrameHeight;

            int tileIdx = 0;
            for (int gr = 0; gr < gridRows; gr++)
            {
                for (int gc = 0; gc < gridColumns; gc++)
                {
                    var tile = frames[tileIdx++];
                    for (int plane = 0; plane < numPlanes; plane++)
                    {
                        int subX = plane > 0 && seq.SubsamplingX ? 1 : 0;
                        int subY = plane > 0 && seq.SubsamplingY ? 1 : 0;

                        // contentW/H is the grid's nominal per-tile cell size, used for placement stepping
                        // (spec-mandated uniform across tiles); tileAvailW/H is bounded by what THIS tile
                        // actually decoded to, since a shared/reused tile item (a legal HEIF construct --
                        // e.g. one item referenced by both the color and alpha grids' dimg associations)
                        // isn't guaranteed to match the first tile's dimensions.
                        int contentW = (tileContentW + subX) >> subX;
                        int contentH = (tileContentH + subY) >> subY;
                        int tileAvailW = (tile.Frame.UpscaledWidth + subX) >> subX;
                        int tileAvailH = (tile.Frame.FrameHeight + subY) >> subY;

                        int destBaseX = gc * contentW;
                        int destBaseY = gr * contentH;
                        int copyW = Math.Min(Math.Min(contentW, tileAvailW), outWidths[plane] - destBaseX);
                        int copyH = Math.Min(Math.Min(contentH, tileAvailH), outHeights[plane] - destBaseY);
                        if (copyW <= 0 || copyH <= 0)
                        {
                            continue;
                        }

                        CopyRegion(tile.Planes[plane], tile.PlaneWidths[plane], outPlanes[plane], outWidths[plane], destBaseX, destBaseY, copyW, copyH);
                    }
                }
            }
        }

        return new CompositedFrame
        {
            Planes = outPlanes,
            Widths = outWidths,
            Heights = outHeights,
            Sequence = seq,
        };
    }

    /// <summary>
    /// Copies a <paramref name="w"/> x <paramref name="h"/> region from <paramref name="src"/> (row 0,
    /// col 0) into <paramref name="dst"/> at (<paramref name="destBaseY"/>, <paramref name="destBaseX"/>).
    /// Clamps <paramref name="w"/>/<paramref name="h"/> against both arrays' actual lengths -- a last-resort
    /// safety net, independent of (and cheaper to reason about than) whether every upstream geometry
    /// computation (grid cell size vs. an individual tile's own decoded dimensions, which a shared/reused
    /// tile item is not guaranteed to match) got every edge case right.
    /// </summary>
    private static void CopyRegion(int[] src, int srcStride, int[] dst, int dstStride, int destBaseX, int destBaseY, int w, int h)
    {
        if (srcStride <= 0 || dstStride <= 0)
        {
            return;
        }

        w = Math.Min(w, srcStride);
        w = Math.Min(w, dstStride - destBaseX);
        h = Math.Min(h, src.Length / srcStride);
        h = Math.Min(h, dst.Length / dstStride - destBaseY);

        if (w <= 0 || h <= 0)
        {
            return;
        }

        for (int y = 0; y < h; y++)
        {
            Array.Copy(src, y * srcStride, dst, ((destBaseY + y) * dstStride) + destBaseX, w);
        }
    }
}
