namespace PeachImage.Formats.Shared.Metadata;

/// <summary>Picks the ICC profile and EXIF block an encoder should write for an image, honouring the caller's <see cref="EncoderOptions"/>.</summary>
internal static class EncodableMetadata
{
    private static ReadOnlySpan<byte> ExifPrefix => "Exif\0\0"u8;

    private static ReadOnlySpan<byte> XmpSignature => "http://ns.adobe.com/xap/1.0/"u8;

    /// <summary>The first ICC profile on <paramref name="image"/>, or <see langword="null"/> when there is none or <see cref="EncoderOptions.WritesIccProfile"/> is off.</summary>
    public static byte[]? Icc(Image image, EncoderOptions options)
    {
        if (!options.WritesIccProfile)
        {
            return null;
        }

        foreach (var profile in image.Metadata.Profiles)
        {
            if (profile.Kind == MetadataProfileKind.Icc && profile.Data.Length > 0)
            {
                return profile.Data;
            }
        }

        return null;
    }

    /// <summary>
    /// The first XMP packet on <paramref name="image"/> (without any <c>http://ns.adobe.com/xap/1.0/</c> JPEG header), or
    /// <see langword="null"/> when there is none or <see cref="EncoderOptions.IncludeMetadata"/> is off.
    /// </summary>
    public static byte[]? Xmp(Image image, EncoderOptions options)
    {
        if (!options.IncludeMetadata)
        {
            return null;
        }

        foreach (var profile in image.Metadata.Profiles)
        {
            if (profile.Kind == MetadataProfileKind.Xmp && profile.Data.Length > 0)
            {
                return profile.Data.AsSpan().StartsWith(XmpSignature) ? profile.Data[(XmpSignature.Length + 1)..] : profile.Data;
            }
        }

        return null;
    }

    /// <summary>
    /// The first EXIF block on <paramref name="image"/> as a bare TIFF-structured payload (any <c>Exif\0\0</c> prefix removed), or
    /// <see langword="null"/> when there is none or <see cref="EncoderOptions.IncludeMetadata"/> is off. A JPEG decode files every
    /// <c>APP1</c> segment under <see cref="MetadataProfileKind.Exif"/>, XMP packets included; those are not EXIF and are skipped.
    /// </summary>
    public static byte[]? Exif(Image image, EncoderOptions options)
    {
        if (!options.IncludeMetadata)
        {
            return null;
        }

        foreach (var profile in image.Metadata.Profiles)
        {
            if (profile.Kind != MetadataProfileKind.Exif || profile.Data.AsSpan().StartsWith(XmpSignature))
            {
                continue;
            }

            var data = profile.Data.AsSpan();
            if (data.StartsWith(ExifPrefix))
            {
                data = data[ExifPrefix.Length..];
            }

            if (data.Length >= 8 && ((data[0] == (byte)'I' && data[1] == (byte)'I') || (data[0] == (byte)'M' && data[1] == (byte)'M')))
            {
                return data.ToArray();
            }
        }

        return null;
    }
}
