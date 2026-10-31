namespace PeachImage.Formats.Shared.Metadata;

/// <summary>What an image's EXIF, XMP and IPTC metadata say about where it was taken.</summary>
internal readonly record struct LocationInfo(bool ExifGps, ExifLocation? Exif, bool Xmp, bool Iptc)
{
    /// <summary>Whether any of the three sources names a location.</summary>
    public bool HasLocation => ExifGps || Xmp || Iptc;

    /// <summary>Combines two results: a source reports a location if either input did.</summary>
    public LocationInfo Merge(LocationInfo other) =>
        new(ExifGps || other.ExifGps, Exif ?? other.Exif, Xmp || other.Xmp, Iptc || other.Iptc);
}
