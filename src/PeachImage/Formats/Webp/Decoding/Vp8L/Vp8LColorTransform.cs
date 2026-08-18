using PeachImage.Formats.Webp.Kernels;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Inverts VP8L's cross-color transform: per tile (side <c>1 &lt;&lt; transform.Bits</c>), three signed-8-bit
/// multipliers packed into the tile parameter sub-image's pixel (<c>greenToRed</c> = bits 0-7,
/// <c>greenToBlue</c> = bits 8-15, <c>redToBlue</c> = bits 16-23 — verified against libwebp's
/// <c>ColorCodeToMultipliers</c>, <c>src/dsp/lossless.c</c>) predict red/blue from green.
/// </summary>
/// <remarks>
/// The per-pixel inverse (each tile run's <see cref="IVp8LTransformKernel.ColorTransformInverse"/> call) is
/// vectorized: its "same-pixel dependency" — the second blue delta reads the just-recomputed, already-masked
/// red channel — is within one pixel's 3-step chain, not across pixels, so every pixel in a run is
/// independent and safe to process lane-parallel. Only the outer tile walk (which multipliers apply to which
/// run) stays here.
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
        var kernel = Vp8LTransformKernelSelector.Instance;

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
                kernel.ColorTransformInverse(row.Slice(x, runLength), greenToRed, greenToBlue, redToBlue);
                x += runLength;
                tileIndex++;
            }
        }
    }

    private static (sbyte GreenToRed, sbyte GreenToBlue, sbyte RedToBlue) ExtractMultipliers(uint colorCode) =>
        ((sbyte)(colorCode & 0xFF), (sbyte)((colorCode >> 8) & 0xFF), (sbyte)((colorCode >> 16) & 0xFF));
}
