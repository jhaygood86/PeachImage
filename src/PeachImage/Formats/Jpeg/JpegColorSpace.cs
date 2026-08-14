namespace PeachImage.Formats.Jpeg;

/// <summary>The color space a JPEG frame's samples are encoded in.</summary>
public enum JpegColorSpace
{
    /// <summary>Single-component grayscale.</summary>
    Grayscale,

    /// <summary>Three-component luma/chroma (the overwhelmingly common case for color JPEGs).</summary>
    YCbCr,

    /// <summary>Three-component direct RGB (rare; signaled by an Adobe APP14 marker with transform=0).</summary>
    Rgb,

    /// <summary>Four-component cyan/magenta/yellow/key.</summary>
    Cmyk,

    /// <summary>Four-component luma/chroma/key (signaled by an Adobe APP14 marker with transform=2).</summary>
    Ycck,
}
