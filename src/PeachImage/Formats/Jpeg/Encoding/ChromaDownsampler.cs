using System.Buffers;

namespace PeachImage.Formats.Jpeg.Encoding;

/// <summary>Box-filter (average) chroma downsampler, used when encoding with subsampled chroma.</summary>
internal static class ChromaDownsampler
{
    /// <summary>
    /// Downsamples <paramref name="source"/> by the given integer ratios, averaging each source block.
    /// The result is rented from <see cref="ArrayPool{T}"/> and tracked in <paramref name="rented"/> for the
    /// caller to return once the frame is fully encoded (see <see cref="Encoding.FrameEncoder"/>'s remarks).
    /// </summary>
    public static byte[] Downsample(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int horizontalRatio, int verticalRatio, out int outputWidth, out int outputHeight, List<byte[]> rented)
    {
        outputWidth = (sourceWidth + horizontalRatio - 1) / horizontalRatio;
        outputHeight = (sourceHeight + verticalRatio - 1) / verticalRatio;
        var result = ArrayPool<byte>.Shared.Rent(outputWidth * outputHeight);
        rented.Add(result);

        // 2x2 (4:2:0) and 2x1 (4:2:2) are the only ratios real-world JPEGs use — a fixed-shape sum/shift
        // avoids the generic loop's per-pixel edge-of-source bounds check and integer division. Only valid
        // when every output pixel's source block is fully in-bounds (source dimensions exactly even for the
        // ratio); the last partial row/column at odd source dimensions falls back to the general path below.
        if (horizontalRatio == 2 && verticalRatio == 2)
        {
            int fullBlockHeight = sourceHeight / 2;
            int fullBlockWidth = sourceWidth / 2;
            DownsampleBox2x2(source, sourceWidth, result, outputWidth, fullBlockWidth, fullBlockHeight);
            DownsampleGeneric(source, sourceWidth, sourceHeight, result, outputWidth, 2, 2, fullBlockWidth, fullBlockHeight, outputWidth, outputHeight);
            return result;
        }

        if (horizontalRatio == 2 && verticalRatio == 1)
        {
            // verticalRatio == 1 means every source row maps 1:1 to an output row (outputHeight == sourceHeight
            // exactly, no partial trailing row is possible), so DownsampleBox2x1 above already covers every row
            // for the fully in-bounds columns; skipY = outputHeight marks all rows as already fast-pathed, so
            // only the trailing partial column (when sourceWidth is odd) falls through to the generic path.
            int fullBlockWidth = sourceWidth / 2;
            DownsampleBox2x1(source, sourceWidth, sourceHeight, result, outputWidth, fullBlockWidth);
            DownsampleGeneric(source, sourceWidth, sourceHeight, result, outputWidth, 2, 1, fullBlockWidth, outputHeight, outputWidth, outputHeight);
            return result;
        }

        DownsampleGeneric(source, sourceWidth, sourceHeight, result, outputWidth, horizontalRatio, verticalRatio, 0, 0, outputWidth, outputHeight);
        return result;
    }

    private static void DownsampleBox2x2(ReadOnlySpan<byte> source, int sourceWidth, Span<byte> result, int outputWidth, int fullBlockWidth, int fullBlockHeight)
    {
        for (int y = 0; y < fullBlockHeight; y++)
        {
            int sy0 = y * 2 * sourceWidth;
            int sy1 = sy0 + sourceWidth;
            int dy = y * outputWidth;
            for (int x = 0; x < fullBlockWidth; x++)
            {
                int sx = x * 2;
                int sum = source[sy0 + sx] + source[sy0 + sx + 1] + source[sy1 + sx] + source[sy1 + sx + 1];
                result[dy + x] = (byte)((sum + 2) >> 2);
            }
        }
    }

    private static void DownsampleBox2x1(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, Span<byte> result, int outputWidth, int fullBlockWidth)
    {
        for (int y = 0; y < sourceHeight; y++)
        {
            int sy = y * sourceWidth;
            int dy = y * outputWidth;
            for (int x = 0; x < fullBlockWidth; x++)
            {
                int sx = x * 2;
                int sum = source[sy + sx] + source[sy + sx + 1];
                result[dy + x] = (byte)((sum + 1) >> 1);
            }
        }
    }

    /// <summary>General box-filter path: handles arbitrary ratios and any output pixel whose source block runs past the source's edge (skipped by the specialized paths above, which only cover fully in-bounds blocks).</summary>
    private static void DownsampleGeneric(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, Span<byte> result, int outputWidth, int horizontalRatio, int verticalRatio, int skipX, int skipY, int fullOutputWidth, int fullOutputHeight)
    {
        for (int y = 0; y < fullOutputHeight; y++)
        {
            bool rowInFastPath = y < skipY;
            for (int x = rowInFastPath ? skipX : 0; x < fullOutputWidth; x++)
            {
                int sum = 0;
                int count = 0;
                for (int dy = 0; dy < verticalRatio; dy++)
                {
                    int sy = (y * verticalRatio) + dy;
                    if (sy >= sourceHeight)
                    {
                        continue;
                    }

                    for (int dx = 0; dx < horizontalRatio; dx++)
                    {
                        int sx = (x * horizontalRatio) + dx;
                        if (sx >= sourceWidth)
                        {
                            continue;
                        }

                        sum += source[(sy * sourceWidth) + sx];
                        count++;
                    }
                }

                result[(y * outputWidth) + x] = (byte)((sum + (count / 2)) / Math.Max(count, 1));
            }
        }
    }
}
