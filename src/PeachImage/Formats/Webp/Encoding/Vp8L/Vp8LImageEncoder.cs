using PeachImage.Formats.Webp.Decoding.Vp8L;
using PeachImage.Formats.Webp.Internal;
using PeachImage.Formats.Webp.Kernels;

namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>
/// Assembles one complete VP8L chunk payload from a flat ARGB pixel buffer: the 5-byte header, transform
/// declarations, and entropy-coded pixel stream — the encode-side mirror of
/// <see cref="Decoding.Vp8L.Vp8LDecoder"/>. Owns *which* transforms to declare and in what order (mirroring
/// how <see cref="Decoding.Vp8L.Vp8LDecoder.DecodeCore"/> owns unwinding them), leaving the entropy coding
/// itself to <see cref="Vp8LImageStreamWriter"/>.
/// </summary>
internal static class Vp8LImageEncoder
{
    private const byte Signature = 0x2F;
    private const int PredictorModeCount = 14;

    /// <summary>
    /// Encodes <paramref name="argb"/>'s first <paramref name="pixelCount"/> elements. Takes ownership of
    /// <paramref name="argb"/> and mutates it directly (no defensive copy) -- callers must not read it again
    /// afterward. <paramref name="argb"/> may be a pool-rented buffer longer than <paramref name="pixelCount"/>
    /// (an <see cref="System.Buffers.ArrayPool{T}"/> contract); every access here is bounded by
    /// <paramref name="pixelCount"/> or <c>width*height</c> explicitly, never by the array's own
    /// <see cref="Array.Length"/>, until a transform reassigns <c>pixels</c> to a fresh, exactly-sized buffer.
    /// </summary>
    public static byte[] Encode(uint[] argb, int pixelCount, int width, int height, bool hasAlpha, WebpEncoderOptions options)
    {
        var writer = new Vp8LBitWriter();
        writer.WriteBits((uint)(width - 1), 14);
        writer.WriteBits((uint)(height - 1), 14);
        writer.WriteBits(hasAlpha ? 1u : 0u, 1);
        writer.WriteBits(0, 3); // version_number -- always 0.

        uint[] pixels = argb;
        int workingWidth = width;
        bool colorIndexingApplied;

        uint[]? palette = TryBuildPalette(argb.AsSpan(0, pixelCount));
        if (palette is not null)
        {
            WriteColorIndexingTransform(writer, ref pixels, ref workingWidth, height, palette, options);
            colorIndexingApplied = true;
        }
        else
        {
            WriteSubtractGreenTransform(writer, pixels.AsSpan(0, pixelCount));
            WritePredictorTransform(writer, ref pixels, workingWidth, height, options);
            colorIndexingApplied = false;
        }

        writer.WriteBits(0, 1); // No more transforms.

        // pixels.Length is not trusted here: when the predictor branch ran, pixels is a pool-rented buffer
        // (see ApplyPredictorForward) that may be longer than workingWidth*height (an ArrayPool contract).
        // workingWidth*height is correct either way -- exactly the palette-narrowed array's own size in the
        // color-indexing branch, and the true pixel count regardless of the rented array's real length here.
        int finalPixelCount = workingWidth * height;

        try
        {
            Vp8LImageStreamWriter.WriteImageStream(writer, pixels, finalPixelCount, workingWidth, options, allowColorCache: !colorIndexingApplied, allowRecursion: true);
        }
        finally
        {
            if (!colorIndexingApplied)
            {
                // Only the predictor branch's residual buffer is pool-rented; the palette branch's narrowed
                // buffer is a plain, exactly-sized allocation with nothing to return.
                WebpBufferPool.SharedUInt32.Return(pixels);
            }
        }

        byte[] body = writer.ToArray();
        byte[] result = new byte[1 + body.Length];
        result[0] = Signature;
        body.CopyTo(result.AsSpan(1));
        return result;
    }

    // -- Color-indexing (palette) transform --------------------------------------------------------------

    /// <summary>Collects the image's distinct colors, sorted by luma so consecutive palette entries stay close together (minimizing the delta-encoded palette sub-image's magnitudes). Returns <see langword="null"/> once more than <see cref="WebpDecodingLimits.MaxColorIndexingPaletteSize"/> distinct colors are seen.</summary>
    private static uint[]? TryBuildPalette(ReadOnlySpan<uint> pixels)
    {
        var distinct = new HashSet<uint>();
        foreach (uint pixel in pixels)
        {
            distinct.Add(pixel);
            if (distinct.Count > WebpDecodingLimits.MaxColorIndexingPaletteSize)
            {
                return null;
            }
        }

        var palette = new uint[distinct.Count];
        distinct.CopyTo(palette);
        Array.Sort(palette, (a, b) => Luma(a).CompareTo(Luma(b)));
        return palette;
    }

    private static int Luma(uint argb)
    {
        int r = (int)(argb >> 16) & 0xFF;
        int g = (int)(argb >> 8) & 0xFF;
        int b = (int)argb & 0xFF;
        return (299 * r) + (587 * g) + (114 * b);
    }

    private static void WriteColorIndexingTransform(Vp8LBitWriter writer, ref uint[] pixels, ref int workingWidth, int height, uint[] palette, WebpEncoderOptions options)
    {
        int numColors = palette.Length;
        int bits = numColors switch
        {
            > 16 => 0,
            > 4 => 1,
            > 2 => 2,
            _ => 3,
        };

        writer.WriteBits(1, 1);
        writer.WriteBits((uint)Vp8LTransformType.ColorIndexing, 2);
        writer.WriteBits((uint)(numColors - 1), 8);

        // The palette sub-image is delta-encoded (each entry relative to the previous), the exact inverse of
        // Vp8LColorIndexingTransform.ExpandPalette's cumulative sum.
        var deltas = new uint[numColors];
        deltas[0] = palette[0];
        for (int i = 1; i < numColors; i++)
        {
            deltas[i] = Vp8LPixelMath.SubtractWrapping(palette[i], palette[i - 1]);
        }

        Vp8LImageStreamWriter.WriteImageStream(writer, deltas, numColors, numColors, options, allowColorCache: false, allowRecursion: false);

        var colorToIndex = new Dictionary<uint, int>(numColors);
        for (int i = 0; i < numColors; i++)
        {
            colorToIndex[palette[i]] = i;
        }

        int originalWidth = workingWidth;
        int narrowedWidth = Vp8LMetaHuffmanImage.SubSampleSize(originalWidth, bits);
        var narrowed = new uint[narrowedWidth * height];

        // Only the green byte is ever read back by Vp8LColorIndexingTransform.ApplyInverse -- alpha/red/blue
        // are left 0 (their default), minimizing their Huffman-coding cost for free. Bounded by
        // originalWidth*height (not pixels.Length), since pixels may still be the caller's pool-rented,
        // possibly-oversized buffer at this point.
        if (bits == 0)
        {
            int pixelCount = originalWidth * height;
            for (int i = 0; i < pixelCount; i++)
            {
                narrowed[i] = (uint)colorToIndex[pixels[i]] << 8;
            }
        }
        else
        {
            int pixelsPerByte = 1 << bits;
            int bitsPerPixel = 8 >> bits;

            for (int y = 0; y < height; y++)
            {
                int srcRowBase = y * originalWidth;
                int dstRowBase = y * narrowedWidth;
                int dstX = 0;
                uint packed = 0;
                int shift = 0;

                for (int x = 0; x < originalWidth; x++)
                {
                    int index = colorToIndex[pixels[srcRowBase + x]];
                    packed |= (uint)index << shift;
                    shift += bitsPerPixel;

                    if (((x + 1) % pixelsPerByte == 0) || x == originalWidth - 1)
                    {
                        narrowed[dstRowBase + dstX] = packed << 8;
                        dstX++;
                        packed = 0;
                        shift = 0;
                    }
                }
            }
        }

        pixels = narrowed;
        workingWidth = narrowedWidth;
    }

    // -- Subtract-green transform --------------------------------------------------------------------------

    private static void WriteSubtractGreenTransform(Vp8LBitWriter writer, Span<uint> pixels)
    {
        writer.WriteBits(1, 1);
        writer.WriteBits((uint)Vp8LTransformType.SubtractGreen, 2);
        Vp8LEncodeKernelSelector.Instance.SubtractGreenForward(pixels);
    }

    // -- Predictor transform --------------------------------------------------------------------------------

    private static void WritePredictorTransform(Vp8LBitWriter writer, ref uint[] pixels, int width, int height, WebpEncoderOptions options)
    {
        int sampleStride = GetPredictorSampleStride(options.CompressionLevel);
        var (bits, tileModes, tileXSize) = SelectPredictorTiling(pixels, width, height, options.CompressionLevel, sampleStride);

        writer.WriteBits(1, 1);
        writer.WriteBits((uint)Vp8LTransformType.Predictor, 2);
        writer.WriteBits((uint)(bits - 2), 3);

        Vp8LImageStreamWriter.WriteImageStream(writer, tileModes, tileModes.Length, tileXSize, options, allowColorCache: false, allowRecursion: false);

        pixels = ApplyPredictorForward(pixels, width, height, tileXSize, bits, tileModes);
    }

    /// <summary>
    /// Fixed tile size (bits=4, 16x16 tiles) for <see cref="WebpCompressionLevel.Default"/>/<see cref="WebpCompressionLevel.Fastest"/>;
    /// for <see cref="WebpCompressionLevel.SmallestSize"/>, trials a few candidate tile sizes and keeps
    /// whichever scores lowest by the same per-tile cost estimate <see cref="ChooseBestPredictorMode"/> uses.
    /// </summary>
    private static (int Bits, uint[] TileModes, int TileXSize) SelectPredictorTiling(uint[] pixels, int width, int height, WebpCompressionLevel level, int sampleStride)
    {
        int[] candidates = level == WebpCompressionLevel.SmallestSize ? [3, 4, 5] : [4];

        int bestBits = candidates[0];
        uint[]? bestTileModes = null;
        int bestTileXSize = 0;
        long bestTotalCost = long.MaxValue;

        foreach (int bits in candidates)
        {
            int tileXSize = Vp8LMetaHuffmanImage.SubSampleSize(width, bits);
            int tileYSize = Vp8LMetaHuffmanImage.SubSampleSize(height, bits);
            var tileModes = new uint[tileXSize * tileYSize];
            long totalCost = 0;

            for (int tileY = 0; tileY < tileYSize; tileY++)
            {
                for (int tileX = 0; tileX < tileXSize; tileX++)
                {
                    var (mode, cost) = ChooseBestPredictorMode(pixels, width, height, tileX, tileY, bits, sampleStride);
                    tileModes[(tileY * tileXSize) + tileX] = (uint)mode << 8;
                    totalCost += cost;
                }
            }

            if (totalCost < bestTotalCost)
            {
                bestTotalCost = totalCost;
                bestBits = bits;
                bestTileModes = tileModes;
                bestTileXSize = tileXSize;
            }
        }

        return (bestBits, bestTileModes!, bestTileXSize);
    }

    /// <summary>
    /// How many interior pixels apart to sample when scoring a tile's candidate predictor modes: 1 (every
    /// pixel) for <see cref="WebpCompressionLevel.SmallestSize"/>, coarser otherwise. Purely a search-cost
    /// knob, not a correctness one -- every predictor mode is exactly invertible regardless of which one gets
    /// picked, so sparser sampling only risks occasionally picking a slightly-less-optimal mode for a tile,
    /// never an incorrect encode.
    /// </summary>
    private static int GetPredictorSampleStride(WebpCompressionLevel level) => level switch
    {
        WebpCompressionLevel.Fastest => 4,
        WebpCompressionLevel.SmallestSize => 1,
        _ => 2,
    };

    /// <summary>
    /// Picks the predictor mode minimizing summed per-channel absolute residuals over a sample of a tile's
    /// *interior* pixels (row 0 and each row's own column 0 always use fixed modes 0/1/2 regardless of tile
    /// data -- see <see cref="Decoding.Vp8L.Vp8LPredictorTransform.ApplyInverse"/> -- so including them here
    /// would skew the choice toward whichever mode happens to match a decision that was never actually theirs
    /// to make). <paramref name="sampleStride"/> &gt; 1 trades estimate precision for search speed -- every
    /// mode's own residual-writing pass in <see cref="ApplyPredictorForward"/> still runs over every pixel
    /// regardless, evaluating only whichever single mode this method picked.
    /// </summary>
    private static (int Mode, long Cost) ChooseBestPredictorMode(uint[] pixels, int width, int height, int tileX, int tileY, int bits, int sampleStride)
    {
        int tileSize = 1 << bits;
        int xStart = tileX << bits;
        int xEnd = Math.Min(xStart + tileSize, width);
        int yStart = tileY << bits;
        int yEnd = Math.Min(yStart + tileSize, height);

        Span<long> cost = stackalloc long[PredictorModeCount];
        bool anyInterior = false;

        for (int y = Math.Max(yStart, 1); y < yEnd; y += sampleStride)
        {
            int rowBase = y * width;
            for (int x = Math.Max(xStart, 1); x < xEnd; x += sampleStride)
            {
                anyInterior = true;
                int index = rowBase + x;
                uint actual = pixels[index];

                for (int mode = 0; mode < PredictorModeCount; mode++)
                {
                    uint predicted = Vp8LPredictorTransform.Predict(mode, pixels, index, width);
                    cost[mode] += AbsChannelDiffSum(actual, predicted);
                }
            }
        }

        if (!anyInterior)
        {
            return (0, 0);
        }

        int bestMode = 0;
        long bestCost = cost[0];
        for (int mode = 1; mode < PredictorModeCount; mode++)
        {
            if (cost[mode] < bestCost)
            {
                bestCost = cost[mode];
                bestMode = mode;
            }
        }

        return (bestMode, bestCost);
    }

    private static long AbsChannelDiffSum(uint a, uint b)
    {
        long sum = Math.Abs((byte)(a >> 24) - (byte)(b >> 24));
        sum += Math.Abs((byte)(a >> 16) - (byte)(b >> 16));
        sum += Math.Abs((byte)(a >> 8) - (byte)(b >> 8));
        sum += Math.Abs((byte)a - (byte)b);
        return sum;
    }

    /// <summary>
    /// Computes every pixel's residual into a fresh buffer (never in place -- <see cref="Vp8LPredictorTransform.Predict"/>
    /// must always read *original* neighbor values, which in-place mutation would corrupt for
    /// later pixels in the same row/column) using the tile-selected mode for interior pixels and the fixed
    /// row-0/column-0 overrides everywhere else, exactly mirroring <see cref="Decoding.Vp8L.Vp8LPredictorTransform.ApplyInverse"/>.
    /// </summary>
    private static uint[] ApplyPredictorForward(uint[] pixels, int width, int height, int tileXSize, int bits, uint[] tileModes)
    {
        // Rented (and later returned by Encode, once WriteImageStream is done reading it) rather than freshly
        // allocated -- for a 1080p image this is ~8 MiB, well past the large-object-heap threshold, and would
        // otherwise be a fresh LOH allocation discarded on every single-image encode. May come back larger
        // than width*height (an ArrayPool contract); every access below is bounded by width/height explicitly.
        var residual = WebpBufferPool.SharedUInt32.Rent(width * height);

        residual[0] = Vp8LPixelMath.SubtractWrapping(pixels[0], 0xFF000000u);

        for (int x = 1; x < width; x++)
        {
            residual[x] = Vp8LPixelMath.SubtractWrapping(pixels[x], pixels[x - 1]);
        }

        for (int y = 1; y < height; y++)
        {
            int rowBase = y * width;
            residual[rowBase] = Vp8LPixelMath.SubtractWrapping(pixels[rowBase], pixels[rowBase - width]);

            int tileY = y >> bits;
            for (int x = 1; x < width; x++)
            {
                int index = rowBase + x;
                int tileX = x >> bits;
                int mode = (int)((tileModes[(tileY * tileXSize) + tileX] >> 8) & 0xF);
                uint predicted = Vp8LPredictorTransform.Predict(mode, pixels, index, width);
                residual[index] = Vp8LPixelMath.SubtractWrapping(pixels[index], predicted);
            }
        }

        return residual;
    }
}
