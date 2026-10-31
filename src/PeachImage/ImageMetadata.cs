using PeachImage.Formats.Shared.Metadata;

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
    /// What the file's metadata scan found when this image was loaded (see <see cref="Image.Load(Stream, DecoderOptions?)"/>);
    /// copied to images derived from it so the answer survives <see cref="Image.Clone"/> and friends.
    /// </summary>
    internal LocationInfo ScannedLocation { get; set; }

    private LocationInfo Location => ScannedLocation.Merge(LocationFromProfiles());

    /// <summary>
    /// Whether this image's metadata says where it was taken: GPS tags in EXIF or XMP, or a place named in IPTC (City,
    /// Sub-location or Province/State). Combines what was found in the file when it was loaded from a JPEG, PNG, WebP,
    /// TIFF, GIF, AVIF or JPEG XL file with anything in <see cref="Profiles"/>; the file is examined by its container structure, so this also covers
    /// metadata that <see cref="Profiles"/> does not expose (for example a PNG <c>eXIf</c> chunk or JPEG IPTC data).
    /// Always <see langword="false"/> when <see cref="DecoderOptions.SkipMetadata"/> was set.
    /// </summary>
    /// <remarks>Evaluated on each call, so avoid calling it in a hot loop on an image with large profiles.</remarks>
    public bool HasLocation => Location.HasLocation;

    /// <summary>The raw EXIF GPS values (see <see cref="PeachImage.ExifLocation"/>), or <see langword="null"/> when the EXIF names none.</summary>
    public ExifLocation? ExifLocation => Location.Exif;

    internal LocationInfo LocationFromProfiles()
    {
        var found = default(LocationInfo);
        foreach (var profile in Profiles)
        {
            switch (profile.Kind)
            {
                case MetadataProfileKind.Exif:
                    // The JPEG decoder files every APP1 segment under Exif, XMP packets included.
                    found = profile.Data.AsSpan().StartsWith("http://ns.adobe.com/xap/1.0/\0"u8)
                        ? found with { Xmp = found.Xmp || XmpLocationScanner.NamesLocation(profile.Data.AsSpan(28)) }
                        : found.Merge(ExifLocationReader.Read(profile.Data));
                    break;
                case MetadataProfileKind.Xmp:
                    found = found with { Xmp = found.Xmp || XmpLocationScanner.NamesLocation(profile.Data) };
                    break;
                case MetadataProfileKind.Iptc:
                    found = found with { Iptc = found.Iptc || IptcLocationReader.ReadPhotoshop(profile.Data) };
                    break;
            }
        }

        return found;
    }

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
