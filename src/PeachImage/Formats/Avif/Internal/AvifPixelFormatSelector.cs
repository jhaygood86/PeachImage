namespace PeachImage.Formats.Avif.Internal;

/// <summary>
/// Chooses the natural (no-override) decoded <see cref="PixelFormat"/> for an AVIF image's bit depth,
/// monochrome flag, and alpha presence. Reuses <c>Png.Internal.PngPixelFormatSelector</c>'s existing
/// gray+alpha promotion convention (there is no dedicated gray+alpha <see cref="PixelFormat"/>, so a
/// monochrome image with alpha promotes to <see cref="PixelFormat.Rgba32"/>/<see cref="PixelFormat.Rgba64"/>
/// with R=G=B=gray) rather than inventing a second one.
/// </summary>
internal static class AvifPixelFormatSelector
{
    public static PixelFormat Choose(int bitDepth, bool monochrome, bool hasAlpha) => (bitDepth, monochrome, hasAlpha) switch
    {
        (8, false, false) => PixelFormat.Rgb24,
        (8, false, true) => PixelFormat.Rgba32,
        (8, true, false) => PixelFormat.Gray8,
        (8, true, true) => PixelFormat.Rgba32,
        (> 8, false, false) => PixelFormat.Rgb48,
        (> 8, false, true) => PixelFormat.Rgba64,
        (> 8, true, false) => PixelFormat.Gray16,
        (> 8, true, true) => PixelFormat.Rgba64,
        _ => throw new AvifDecodingException($"Unsupported AVIF bit depth {bitDepth}."),
    };
}
