namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Inverts VP8L's cross-color transform: per tile (side <c>1 &lt;&lt; transform.Bits</c>), three signed-8-bit
/// multipliers packed into the tile parameter sub-image's pixel (<c>greenToRed</c> = bits 0-7,
/// <c>greenToBlue</c> = bits 8-15, <c>redToBlue</c> = bits 16-23 — verified against libwebp's
/// <c>ColorCodeToMultipliers</c>, <c>src/dsp/lossless.c</c>) predict red/blue from green.
/// </summary>
/// <remarks>
/// Deliberately left scalar-only: unlike the subtract-green transform and the predictor transform's "top"
/// mode, this has a genuine same-pixel dependency across channels — the second blue delta reads the
/// *just-recomputed, already-masked* red channel, not the original one (see <see cref="InversePixel"/>) — so
/// a correct vectorization would need a real widen/multiply/shift/narrow pipeline with that ordering
/// preserved exactly. Given this is per-tile (not per-image) constant data anyway, the risk of a subtly
/// wrong SIMD version wasn't judged worth it for this pass; a future pass could widen to signed 16-bit lanes
/// and multiply-shift-narrow per <see cref="PeachImage.Formats.Webp.Kernels.IVp8LTransformKernel"/>'s pattern.
/// </remarks>
internal static class Vp8LColorTransform
{
    public static void ApplyInverse(Span<uint> pixels, Vp8LTransform transform)
    {
        int width = transform.Xsize;
        int height = transform.Ysize;
        int bits = transform.Bits;
        int tileWidth = 1 << bits;
        var tileData = transform.Data!;
        int tilesPerRow = Vp8LMetaHuffmanImage.SubSampleSize(width, bits);

        for (int y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * width, width);
            var tileRow = tileData.AsSpan((y >> bits) * tilesPerRow, tilesPerRow);

            int x = 0;
            int tileIndex = 0;
            while (x < width)
            {
                int runLength = Math.Min(tileWidth, width - x);
                var (greenToRed, greenToBlue, redToBlue) = ExtractMultipliers(tileRow[tileIndex]);
                InverseRun(row.Slice(x, runLength), greenToRed, greenToBlue, redToBlue);
                x += runLength;
                tileIndex++;
            }
        }
    }

    private static (sbyte GreenToRed, sbyte GreenToBlue, sbyte RedToBlue) ExtractMultipliers(uint colorCode) =>
        ((sbyte)(colorCode & 0xFF), (sbyte)((colorCode >> 8) & 0xFF), (sbyte)((colorCode >> 16) & 0xFF));

    private static void InverseRun(Span<uint> row, sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = InversePixel(row[i], greenToRed, greenToBlue, redToBlue);
        }
    }

    private static uint InversePixel(uint argb, sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue)
    {
        sbyte green = (sbyte)(argb >> 8);
        int red = (int)(argb >> 16) & 0xFF;
        int blue = (int)argb & 0xFF;

        red = (red + ColorTransformDelta(greenToRed, green)) & 0xFF;
        blue = (blue + ColorTransformDelta(greenToBlue, green)) & 0xFF;
        blue = (blue + ColorTransformDelta(redToBlue, (sbyte)red)) & 0xFF;

        return (argb & 0xFF00FF00u) | ((uint)red << 16) | (uint)blue;
    }

    private static int ColorTransformDelta(sbyte multiplier, sbyte color) => (multiplier * color) >> 5;
}
