namespace PeachImage;

/// <summary>Identifies the kind of data carried by a <see cref="RawMetadataProfile"/>.</summary>
public enum MetadataProfileKind
{
    /// <summary>Exchangeable Image File Format (EXIF) data.</summary>
    Exif,

    /// <summary>An embedded ICC color profile.</summary>
    Icc,

    /// <summary>Extensible Metadata Platform (XMP) data.</summary>
    Xmp,

    /// <summary>JFIF header data.</summary>
    Jfif,

    /// <summary>An Adobe APP14 marker segment.</summary>
    AdobeApp14,

    /// <summary>A PNG <c>gAMA</c> chunk's raw 4-byte value (image gamma × 100000, big-endian).</summary>
    Gamma,

    /// <summary>A PNG <c>cHRM</c> chunk's raw 32-byte value (8 big-endian chromaticity values × 100000).</summary>
    Chromaticity,

    /// <summary>A PNG <c>sRGB</c> chunk's raw 1-byte rendering-intent value.</summary>
    Srgb,

    /// <summary>A PNG <c>tIME</c> chunk's raw 7-byte last-modification timestamp.</summary>
    Time,

    /// <summary>A PNG <c>bKGD</c> chunk's raw, color-type-dependent background color value.</summary>
    Background,

    /// <summary>A text comment (PNG <c>tEXt</c>/<c>zTXt</c>/<c>iTXt</c>). See <see cref="PeachImage.Formats.Png.PngTextEntry"/> for a structured view.</summary>
    Text,

    /// <summary>A profile whose kind was not recognized by the decoder.</summary>
    Unknown,
}
