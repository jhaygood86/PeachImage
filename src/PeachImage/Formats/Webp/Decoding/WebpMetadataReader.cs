namespace PeachImage.Formats.Webp.Decoding;

/// <summary>
/// Captures WebP's <c>ICCP</c>/<c>EXIF</c>/<c>XMP </c> chunk payloads into <see cref="ImageMetadata"/> verbatim.
/// Unlike PNG's <c>iCCP</c> chunk, WebP's <c>ICCP</c> payload is the raw ICC profile bytes with no
/// zlib compression or keyword prefix to strip — likewise <c>EXIF</c>/<c>XMP </c> are the raw TIFF/XML
/// bytes with no wrapping prefix, so no decompression/parsing step is needed here.
/// </summary>
internal static class WebpMetadataReader
{
    public static void AddIcc(ImageMetadata metadata, byte[] data) =>
        metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = data });

    public static void AddExif(ImageMetadata metadata, byte[] data) =>
        metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = data });

    public static void AddXmp(ImageMetadata metadata, byte[] data) =>
        metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Xmp, Data = data });
}
