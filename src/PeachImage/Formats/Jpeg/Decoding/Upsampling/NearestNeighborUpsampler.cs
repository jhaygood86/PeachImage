using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jpeg.Decoding.Upsampling;

/// <summary>Fast box-replication upsampler: each source sample is repeated across its <c>horizontalRatio x verticalRatio</c> block.</summary>
internal sealed class NearestNeighborUpsampler : IChromaUpsampler
{
    public void Upsample(
        byte[] source, int sourceWidth, int sourceHeight,
        byte[] destination, int destinationWidth, int destinationHeight,
        int horizontalRatio, int verticalRatio)
    {
        if (horizontalRatio == 1 && verticalRatio == 1)
        {
            RowParallel.For(destinationHeight, y =>
            {
                int srcY = Math.Min(y, sourceHeight - 1);
                source.AsSpan(srcY * sourceWidth, sourceWidth).CopyTo(destination.AsSpan(y * destinationWidth, sourceWidth));
            });

            return;
        }

        RowParallel.For(destinationHeight, y =>
        {
            int srcY = Math.Min(y / verticalRatio, sourceHeight - 1);
            var srcRow = source.AsSpan(srcY * sourceWidth, sourceWidth);
            var dstRow = destination.AsSpan(y * destinationWidth, destinationWidth);

            for (int x = 0; x < destinationWidth; x++)
            {
                int srcX = Math.Min(x / horizontalRatio, sourceWidth - 1);
                dstRow[x] = srcRow[srcX];
            }
        });
    }
}
