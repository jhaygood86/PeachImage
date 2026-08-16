using System.Buffers;

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

        /// <summary>How many of <see cref="Planes"/>' three slots are actually populated (1 for monochrome, 3 otherwise) -- needed by <see cref="ReturnPlanes"/> since the unused slots were never rented and must not be returned.</summary>
        public required int NumPlanes { get; init; }
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

        // Rented, not `new`'d: this buffer's lifetime ends when Av1YuvToRgbConverter.Convert finishes
        // reading it (see AvifDecoder.Decode, which returns it via ReturnPlanes right after). A rented
        // array may be larger than requested, so every reader here and downstream must use the tracked
        // outWidths/outHeights, never Length -- see CopyRegion's own remarks for why that already had to
        // be true regardless of pooling. Explicitly cleared to match `new int[]`'s zero-init: a source
        // tile smaller than the declared output (a mismatched/reused grid tile item, or the last row/
        // column of an overhanging grid) leaves copyW/copyH below outWidths/outHeights, so CopyRegion
        // below does not necessarily write every element.
        var outPlanes = new int[3][];
        var outWidths = new int[3];
        var outHeights = new int[3];
        for (int plane = 0; plane < numPlanes; plane++)
        {
            int subX = plane > 0 && seq.SubsamplingX ? 1 : 0;
            int subY = plane > 0 && seq.SubsamplingY ? 1 : 0;
            outWidths[plane] = (outWidth + subX) >> subX;
            outHeights[plane] = (outHeight + subY) >> subY;
            int length = outWidths[plane] * outHeights[plane];
            outPlanes[plane] = ArrayPool<int>.Shared.Rent(length);
            Array.Clear(outPlanes[plane], 0, length);
        }

        if (gridRows == 1 && gridColumns == 1)
        {
            var single = frames[0];
            for (int plane = 0; plane < numPlanes; plane++)
            {
                int copyW = Math.Min(outWidths[plane], single.PlaneWidths[plane]);
                int copyH = Math.Min(outHeights[plane], single.PlaneHeights[plane]);
                CopyRegion(single.Planes[plane], single.PlaneWidths[plane], single.PlaneHeights[plane], outPlanes[plane], outWidths[plane], outHeights[plane], 0, 0, copyW, copyH);
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

                        CopyRegion(tile.Planes[plane], tile.PlaneWidths[plane], tile.PlaneHeights[plane], outPlanes[plane], outWidths[plane], outHeights[plane], destBaseX, destBaseY, copyW, copyH);
                    }
                }
            }
        }

        // Every frames[i].Planes[plane] has now been read for the last time (frames is local and never
        // escapes this method) -- return each to the pool it came from (Av1FrameDecoder.Decode's own
        // pipeline). Guarded per-plane by numPlanes since a monochrome tile never populated slots 1/2.
        foreach (var frame in frames)
        {
            for (int plane = 0; plane < numPlanes; plane++)
            {
                ArrayPool<int>.Shared.Return(frame.Planes[plane]);
            }
        }

        return new CompositedFrame
        {
            Planes = outPlanes,
            Widths = outWidths,
            Heights = outHeights,
            Sequence = seq,
            NumPlanes = numPlanes,
        };
    }

    /// <summary>Returns a <see cref="CompositedFrame"/>'s pool-rented <see cref="CompositedFrame.Planes"/> once the caller is done reading them (after YUV conversion).</summary>
    public static void ReturnPlanes(CompositedFrame frame)
    {
        for (int plane = 0; plane < frame.NumPlanes; plane++)
        {
            ArrayPool<int>.Shared.Return(frame.Planes[plane]);
        }
    }

    /// <summary>
    /// Copies a <paramref name="w"/> x <paramref name="h"/> region from <paramref name="src"/> (row 0,
    /// col 0) into <paramref name="dst"/> at (<paramref name="destBaseY"/>, <paramref name="destBaseX"/>).
    /// Clamps <paramref name="w"/>/<paramref name="h"/> against the caller-supplied logical
    /// <paramref name="srcHeight"/>/<paramref name="dstHeight"/> -- a last-resort safety net, independent
    /// of (and cheaper to reason about than) whether every upstream geometry computation (grid cell size
    /// vs. an individual tile's own decoded dimensions, which a shared/reused tile item is not guaranteed
    /// to match) got every edge case right. Deliberately not <c>src.Length</c>/<c>dst.Length</c>: both
    /// arrays may be rented from <see cref="ArrayPool{T}"/> and therefore larger than their logical size.
    /// </summary>
    private static void CopyRegion(int[] src, int srcStride, int srcHeight, int[] dst, int dstStride, int dstHeight, int destBaseX, int destBaseY, int w, int h)
    {
        if (srcStride <= 0 || dstStride <= 0)
        {
            return;
        }

        w = Math.Min(w, srcStride);
        w = Math.Min(w, dstStride - destBaseX);
        h = Math.Min(h, srcHeight);
        h = Math.Min(h, dstHeight - destBaseY);

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
