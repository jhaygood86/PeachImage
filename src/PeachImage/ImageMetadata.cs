namespace PeachImage;

/// <summary>Format-agnostic metadata associated with an <see cref="Image"/>.</summary>
public sealed class ImageMetadata
{
    /// <summary>The horizontal resolution, in pixels per unit, if known.</summary>
    public double? HorizontalResolution { get; set; }

    /// <summary>The vertical resolution, in pixels per unit, if known.</summary>
    public double? VerticalResolution { get; set; }

    /// <summary>Raw metadata profiles (EXIF, ICC, XMP, etc.) captured during decode.</summary>
    public IList<RawMetadataProfile> Profiles { get; } = [];
}
