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

    /// <summary>A profile whose kind was not recognized by the decoder.</summary>
    Unknown,
}
