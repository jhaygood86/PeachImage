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

    /// <summary>
    /// Parses this image's embedded ICC profile, if it has one PeachImage's ICC engine can use, or
    /// <see langword="null"/> if there's no <see cref="MetadataProfileKind.Icc"/> entry in <see cref="Profiles"/>
    /// or the embedded bytes aren't usable (malformed, or an unsupported device color space/transform) —
    /// a missing or unusable profile is a normal, expected outcome here, not an error, so this never throws.
    /// </summary>
    public IccColorProfile? GetIccColorProfile()
    {
        foreach (var profile in Profiles)
        {
            if (profile.Kind == MetadataProfileKind.Icc && IccColorProfile.TryCreate(profile.Data, out var iccProfile))
            {
                return iccProfile;
            }
        }

        return null;
    }
}
