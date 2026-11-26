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
    /// <summary>
    /// The most threads one encode call may use. -1 (the default) leaves it to the library, which fans out over up to
    /// <see cref="Environment.ProcessorCount"/> workers; 1 encodes sequentially on the calling thread. The limit is per call, not
    /// global: concurrent calls each use up to this many, so cap the total with <see cref="TaskScheduler"/>. Does not change the output.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = -1;

    /// <summary>
    /// An optional scheduler the encoder's parallel work runs on, so a host can share one limit across concurrent calls (for example
    /// the <see cref="ConcurrentExclusiveSchedulerPair.ConcurrentScheduler"/> of a pair created with a bounded concurrency level).
    /// <see langword="null"/> (the default) uses the thread pool.
    /// </summary>
    public TaskScheduler? TaskScheduler { get; init; }

    /// <summary>
    /// The bit depth the encoder writes. <see cref="EncodePrecision.Auto"/> (the default) keeps whatever precision the format can carry: a source with
    /// 16-bit or floating-point samples, or one converted to a wide-gamut <see cref="TargetColorSpace"/>, is written at 16 bits where the format
    /// has them (PNG, JPEG XL) and at the format's own depth otherwise; 8-bit sources stay 8-bit.
    /// </summary>
    public EncodePrecision Precision { get; init; }

    /// <summary>
    /// Convert the pixels to this color space before encoding and describe it in the file (an ICC profile where the format has one, plus
    /// <c>cICP</c> in PNG). <see langword="null"/> encodes the pixels as they are with whatever profile the image already carries. The conversion is
    /// fused with the pixel-format change the encoder needs anyway, so it adds no extra pass over the image.
    /// </summary>
    public NamedColorSpace? TargetColorSpace { get; init; }

    /// <summary>Like <see cref="TargetColorSpace"/>, for an RGB matrix/TRC ICC profile of the caller's. Takes precedence when both are set.</summary>
    public IccColorProfile? TargetColorProfile { get; init; }

    internal bool WritesIccProfile => IncludeIccProfile ?? IncludeMetadata;

    /// <summary>The sample bit depth the format was asked to write (0 for the encoder to choose), for codecs with a depth option.</summary>
    internal virtual int RequestedBitDepth => 0;

    /// <summary>Whether the codec stores half-precision float pixels as such rather than widened (JPEG XL, lossless only).</summary>
    internal virtual bool KeepsHalfFloat => false;
}
