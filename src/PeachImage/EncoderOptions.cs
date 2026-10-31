namespace PeachImage;

/// <summary>Base class for format-specific encode options. Format codecs derive their own options from this.</summary>
public abstract class EncoderOptions
{
    /// <summary>
    /// Whether the encoder re-emits metadata captured on the source <see cref="Image"/>: its XMP packet and its EXIF block (JPEG <c>APP1</c>, PNG
    /// <c>eXIf</c>, WebP <c>EXIF</c>, an AVIF <c>Exif</c> item, JPEG XL) and, unless <see cref="IncludeIccProfile"/> says otherwise,
    /// its ICC profile, plus the format's own extras (PNG gamma, text and resolution chunks). GIF, BMP and
    /// animated WebP have nowhere to put an <see cref="Image"/>'s metadata and write none. An EXIF block too large for a JPEG
    /// <c>APP1</c> segment (about 64 KB) is left out. Defaults to <see langword="true"/>.
    /// </summary>
    public bool IncludeMetadata { get; init; } = true;

    /// <summary>
    /// Whether the encoder writes the source image's ICC colour profile, independently of <see cref="IncludeMetadata"/>. When
    /// <see langword="null"/> (the default) the profile is written exactly when <see cref="IncludeMetadata"/> says to and the format's
    /// encoder supports it, as it always has been. Set it to <see langword="true"/> together with <c>IncludeMetadata = false</c> to
    /// keep the colour profile while dropping EXIF, XMP, text and everything else, or to <see langword="false"/> to drop only the profile.
    /// </summary>
    /// <remarks>
    /// Supported by the encoders whose format can carry a profile: JPEG (<c>APP2</c> <c>ICC_PROFILE</c> segments), PNG (<c>iCCP</c>),
    /// WebP (<c>ICCP</c>), AVIF (a <c>colr</c> property) and lossless JPEG XL. GIF, BMP and animated WebP write none: GIF and BMP
    /// have no ICC slot this encoder uses, and an <see cref="AnimatedImage"/> carries no metadata to take one from.
    /// </remarks>
    public bool? IncludeIccProfile { get; init; }

    /// <summary>Whether to write the ICC profile: <see cref="IncludeIccProfile"/> when set, else <see cref="IncludeMetadata"/>.</summary>
    internal bool WritesIccProfile => IncludeIccProfile ?? IncludeMetadata;
}
