namespace PeachImage.Formats.Png.Internal;

/// <summary>The PNG IHDR color type values (spec §11.2.2).</summary>
internal enum PngColorType : byte
{
    Grayscale = 0,
    Truecolor = 2,
    Palette = 3,
    GrayscaleAlpha = 4,
    TruecolorAlpha = 6,
}
