namespace PeachImage.Formats.Png.Internal;

/// <summary>Chooses the natural (no-override) decoded <see cref="PixelFormat"/> for a PNG's IHDR color type/bit depth and whether a <c>tRNS</c> chunk is present.</summary>
internal static class PngPixelFormatSelector
{
    public static PixelFormat Choose(PngHeader header, bool hasTrns) => header.ColorType switch
    {
        PngColorType.Grayscale => header.BitDepth == 16
            ? (hasTrns ? PixelFormat.Rgba64 : PixelFormat.Gray16)
            : (hasTrns ? PixelFormat.Rgba32 : PixelFormat.Gray8),
        PngColorType.Truecolor => header.BitDepth == 16
            ? (hasTrns ? PixelFormat.Rgba64 : PixelFormat.Rgb48)
            : (hasTrns ? PixelFormat.Rgba32 : PixelFormat.Rgb24),
        PngColorType.Palette => hasTrns ? PixelFormat.Rgba32 : PixelFormat.Rgb24,
        PngColorType.GrayscaleAlpha or PngColorType.TruecolorAlpha => header.BitDepth == 16 ? PixelFormat.Rgba64 : PixelFormat.Rgba32,
        _ => throw new PngDecodingException($"Unsupported PNG color type {(byte)header.ColorType}."),
    };
}
