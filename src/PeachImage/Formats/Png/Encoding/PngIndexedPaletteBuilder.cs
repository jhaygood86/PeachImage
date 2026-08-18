using PeachImage.Formats.Shared.Quantization;

namespace PeachImage.Formats.Png.Encoding;

/// <summary>
/// Decides whether a source should encode as indexed (PLTE) color per <see cref="PngEncoderOptions.ColorMode"/>,
/// and if so quantizes it down to a palette plus a per-pixel index array. Reuses the same median-cut
/// quantizer, k-d tree nearest-color mapper, and Floyd-Steinberg ditherer GIF encoding uses — the algorithm
/// doesn't care which container format the result ends up in.
/// </summary>
internal static class PngIndexedPaletteBuilder
{
    // Mirrors GIF encoding's hardcoded threshold: pixels at or above this alpha are opaque, below are transparent.
    private const byte AlphaThreshold = 128;

    public sealed record Plan(byte[] Palette, int? TransparentIndex, byte[] Indices, byte BitDepth)
    {
        /// <summary>Total palette slot count actually referenced by <see cref="Indices"/>, including the transparent slot if any.</summary>
        public int UsedColorCount => (Palette.Length / 3) + (TransparentIndex.HasValue ? 1 : 0);
    }

    public static Plan? TryBuild(Image image, PngEncoderOptions options)
    {
        if (image.PixelFormat is not (PixelFormat.Rgb24 or PixelFormat.Rgba32))
        {
            return null;
        }

        if (options.ColorMode == PngColorMode.Truecolor)
        {
            return null;
        }

        // Color-key transparency and a quantized palette target different scenarios (chroma-key on a source
        // with no alpha vs. per-pixel indexed transparency); reconciling them isn't supported, so indexed
        // encoding is skipped entirely rather than guessing.
        if (options.TransparentColor is not null)
        {
            return null;
        }

        int maxColors = Math.Clamp(options.MaxColors, 1, 256);
        bool hasTransparency;

        if (options.ColorMode == PngColorMode.Auto)
        {
            // Auto must stay lossless: a source with any partial (non-0/255) alpha can't be represented by
            // indexed color's binary per-entry transparency, so bail out rather than silently flattening it.
            if (!ColorHistogram.TryGetBinaryTransparency(image, out hasTransparency))
            {
                return null;
            }
        }
        else
        {
            hasTransparency = ColorHistogram.HasTransparentPixel(image, AlphaThreshold);
        }

        int quantizeTarget = Math.Max(1, hasTransparency ? maxColors - 1 : maxColors);

        var histogram = new ColorHistogram();
        if (options.ColorMode == PngColorMode.Auto)
        {
            // Bail out (fall back to grayscale/truecolor) the moment the source is shown to exceed the
            // threshold, instead of paying to scan a whole high-color-count photo just to discard the result.
            if (!histogram.TryAddOpaquePixelsUpTo(image, AlphaThreshold, quantizeTarget))
            {
                return null;
            }
        }
        else
        {
            histogram.AddOpaquePixels(image, AlphaThreshold);
        }

        byte[] palette = MedianCutQuantizer.BuildPalette(histogram, quantizeTarget);

        int? transparentIndex = null;
        int usedColorCount = palette.Length / 3;
        if (hasTransparency)
        {
            transparentIndex = usedColorCount;
            usedColorCount++;
        }

        var paletteTree = new PaletteKdTree(palette);
        byte[] indices = options.Dither
            ? FloydSteinbergDitherer.Map(image, paletteTree, transparentIndex, AlphaThreshold)
            : PaletteMapper.Map(image, paletteTree, transparentIndex, AlphaThreshold);

        return new Plan(palette, transparentIndex, indices, ChooseBitDepth(usedColorCount));
    }

    private static byte ChooseBitDepth(int usedColorCount) => usedColorCount switch
    {
        <= 2 => 1,
        <= 4 => 2,
        <= 16 => 4,
        _ => 8,
    };
}
