namespace PeachImage.Formats.Jpeg.Decoding.Upsampling;

/// <summary>
/// Bilinear ("fancy", libjpeg-style) upsampler: interpolates between neighboring source samples rather than
/// replicating them, producing visibly smoother output for subsampled chroma at a modest extra cost.
/// Falls back to nearest-neighbor for ratios it doesn't specialize (anything other than 1x1/2x1/1x2/2x2).
/// </summary>
internal sealed class TriangleFilterUpsampler : IChromaUpsampler
{
    private static readonly NearestNeighborUpsampler Fallback = new();

    public void Upsample(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destinationWidth, int destinationHeight,
        int horizontalRatio, int verticalRatio)
    {
        if (horizontalRatio == 1 && verticalRatio == 1)
        {
            Fallback.Upsample(source, sourceWidth, sourceHeight, destination, destinationWidth, destinationHeight, 1, 1);
            return;
        }

        if (horizontalRatio is not (1 or 2) || verticalRatio is not (1 or 2))
        {
            Fallback.Upsample(source, sourceWidth, sourceHeight, destination, destinationWidth, destinationHeight, horizontalRatio, verticalRatio);
            return;
        }

        for (int y = 0; y < destinationHeight; y++)
        {
            int srcY = Math.Clamp(y / verticalRatio, 0, sourceHeight - 1);
            int srcYNeighbor = verticalRatio == 2
                ? Math.Clamp(((y / verticalRatio) + (((y & 1) == 0) ? -1 : 1)), 0, sourceHeight - 1)
                : srcY;

            var dstRow = destination.Slice(y * destinationWidth, destinationWidth);
            var srcRowMain = source.Slice(srcY * sourceWidth, sourceWidth);
            var srcRowNeighbor = source.Slice(srcYNeighbor * sourceWidth, sourceWidth);

            for (int x = 0; x < destinationWidth; x++)
            {
                int srcX = Math.Clamp(x / horizontalRatio, 0, sourceWidth - 1);
                int srcXNeighbor = horizontalRatio == 2
                    ? Math.Clamp(((x / horizontalRatio) + (((x & 1) == 0) ? -1 : 1)), 0, sourceWidth - 1)
                    : srcX;

                // 9:3:3:1 weighted blend of the sample and its diagonal/adjacent neighbors, matching libjpeg's
                // "fancy" (triangle-filter) chroma upsampling: mostly the nearest sample, softened toward its neighbors.
                int main = srcRowMain[srcX];
                int horizontalNeighbor = srcRowMain[srcXNeighbor];
                int verticalNeighbor = srcRowNeighbor[srcX];
                int diagonalNeighbor = srcRowNeighbor[srcXNeighbor];

                int blended = ((main * 9) + (horizontalNeighbor * 3) + (verticalNeighbor * 3) + diagonalNeighbor + 8) / 16;
                dstRow[x] = (byte)Math.Clamp(blended, 0, 255);
            }
        }
    }
}
