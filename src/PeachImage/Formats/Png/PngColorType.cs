namespace PeachImage.Formats.Png;

/// <summary>The PNG IHDR color type values (spec §11.2.2).</summary>
public enum PngColorType : byte
{
    /// <summary>One grayscale sample per pixel.</summary>
    Grayscale = 0,

    /// <summary>Red, green, and blue samples per pixel.</summary>
    Truecolor = 2,

    /// <summary>One palette index per pixel; see the file's <c>PLTE</c> chunk.</summary>
    Palette = 3,

    /// <summary>Grayscale and alpha samples per pixel.</summary>
    GrayscaleAlpha = 4,

    /// <summary>Red, green, blue, and alpha samples per pixel.</summary>
    TruecolorAlpha = 6,
}
