namespace PeachImage.Formats.Shared.Compositing;

/// <summary>
/// Copies a rectangular region from one <see cref="Image"/> into another, generic across every
/// <see cref="PixelFormat"/> via <see cref="PixelFormatExtensions.GetBytesPerPixel"/>. Used to composite the
/// scaled source into a <see cref="ResizeMode.Crop"/>/<see cref="ResizeMode.Pad"/> result.
/// </summary>
internal static class ImageBlitter
{
    /// <summary>
    /// Copies the <paramref name="width"/> x <paramref name="height"/> region of <paramref name="source"/>
    /// starting at (<paramref name="sourceX"/>, <paramref name="sourceY"/>) into <paramref name="destination"/>
    /// starting at (<paramref name="destX"/>, <paramref name="destY"/>). Both images must share the same
    /// <see cref="PixelFormat"/>. No hand-rolled SIMD here — each row copy is a plain
    /// <see cref="Span{T}.CopyTo(Span{T})"/>, which already lowers to the runtime's vectorized
    /// <c>Buffer.Memmove</c>, so a custom kernel would only reimplement what's already optimal.
    /// </summary>
    public static void Blit(Image source, Image destination, int sourceX, int sourceY, int destX, int destY, int width, int height)
    {
        int bytesPerPixel = source.PixelFormat.GetBytesPerPixel();
        int rowBytes = width * bytesPerPixel;

        for (int y = 0; y < height; y++)
        {
            var sourceRow = source.GetRowSpan(sourceY + y).Slice(sourceX * bytesPerPixel, rowBytes);
            var destRow = destination.GetRowSpan(destY + y).Slice(destX * bytesPerPixel, rowBytes);
            sourceRow.CopyTo(destRow);
        }
    }
}
