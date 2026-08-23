namespace PeachImage.Formats.Shared.Compositing;

/// <summary>
/// Produces the final <see cref="ResizeMode.Crop"/>/<see cref="ResizeMode.Pad"/> result from an
/// already-scaled intermediate image and a <see cref="FramingPlan"/>. Always allocates a fresh
/// <see cref="Image"/> — never returns the same instance it was given.
/// </summary>
internal static class ImageFramer
{
    /// <summary>Crops the <paramref name="width"/> x <paramref name="height"/> region starting at (<paramref name="offsetX"/>, <paramref name="offsetY"/>) out of <paramref name="source"/>.</summary>
    public static Image Crop(Image source, int width, int height, int offsetX, int offsetY)
    {
        var destination = Image.Create(width, height, source.PixelFormat);
        ImageBlitter.Blit(source, destination, sourceX: offsetX, sourceY: offsetY, destX: 0, destY: 0, width, height);
        return destination;
    }

    /// <summary>Pads <paramref name="source"/> onto a <paramref name="width"/> x <paramref name="height"/> canvas at (<paramref name="offsetX"/>, <paramref name="offsetY"/>), filled with <paramref name="backgroundColor"/> (or the default for <paramref name="source"/>'s <see cref="PixelFormat"/>).</summary>
    public static Image Pad(Image source, int width, int height, int offsetX, int offsetY, (byte R, byte G, byte B, byte A)? backgroundColor)
    {
        var destination = Image.Create(width, height, source.PixelFormat);
        var (r, g, b, a) = ResolveBackgroundColor(backgroundColor, source.PixelFormat);
        PixelFormatFill.Fill(destination, r, g, b, a);
        ImageBlitter.Blit(source, destination, sourceX: 0, sourceY: 0, destX: offsetX, destY: offsetY, source.Width, source.Height);
        return destination;
    }

    /// <summary>
    /// Resolves <see cref="ResizeOptions.BackgroundColor"/>: white when <see langword="null"/> and
    /// <paramref name="format"/> has no alpha channel, transparent when <see langword="null"/> and it does.
    /// </summary>
    internal static (byte R, byte G, byte B, byte A) ResolveBackgroundColor((byte R, byte G, byte B, byte A)? backgroundColor, PixelFormat format) =>
        backgroundColor ?? (format.HasAlpha() ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255, (byte)255));
}
