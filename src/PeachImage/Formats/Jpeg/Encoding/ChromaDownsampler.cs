namespace PeachImage.Formats.Jpeg.Encoding;

/// <summary>Box-filter (average) chroma downsampler, used when encoding with subsampled chroma.</summary>
internal static class ChromaDownsampler
{
    /// <summary>Downsamples <paramref name="source"/> by the given integer ratios, averaging each source block.</summary>
    public static byte[] Downsample(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int horizontalRatio, int verticalRatio, out int outputWidth, out int outputHeight)
    {
        outputWidth = (sourceWidth + horizontalRatio - 1) / horizontalRatio;
        outputHeight = (sourceHeight + verticalRatio - 1) / verticalRatio;
        var result = new byte[outputWidth * outputHeight];

        for (int y = 0; y < outputHeight; y++)
        {
            for (int x = 0; x < outputWidth; x++)
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

        return result;
    }
}
