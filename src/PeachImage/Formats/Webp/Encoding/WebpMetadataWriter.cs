namespace PeachImage.Formats.Webp.Encoding;

/// <summary>Collects ICC/EXIF/XMP profile bytes from <see cref="ImageMetadata"/> for chunk writing — the mirror of <see cref="Webp.Decoding.WebpMetadataReader"/>'s read side.</summary>
internal static class WebpMetadataWriter
{
    /// <summary>
    /// Returns the first ICC/EXIF/XMP profile of each kind found on <paramref name="metadata"/>, ignoring any
    /// additional entries of the same kind -- WebP's container spec disallows more than one <c>ICCP</c>/
    /// <c>EXIF</c>/<c>XMP </c> chunk per file, and this codebase is otherwise lenient about redundant or
    /// inconsistent ancillary data (see <see cref="Webp.Decoding.WebpContainerReader"/>'s own stance).
    /// </summary>
    public static (byte[]? Icc, byte[]? Exif, byte[]? Xmp) CollectProfiles(ImageMetadata metadata)
    {
        byte[]? icc = null;
        byte[]? exif = null;
        byte[]? xmp = null;

        foreach (var profile in metadata.Profiles)
        {
            switch (profile.Kind)
            {
                case MetadataProfileKind.Icc:
                    icc ??= profile.Data;
                    break;

                case MetadataProfileKind.Exif:
                    exif ??= profile.Data;
                    break;

                case MetadataProfileKind.Xmp:
                    xmp ??= profile.Data;
                    break;
            }
        }

        return (icc, exif, xmp);
    }
}
