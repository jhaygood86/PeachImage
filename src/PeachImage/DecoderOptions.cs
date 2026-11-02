namespace PeachImage;

/// <summary>
/// Base class for format-specific decode options. Format codecs derive their own options from this
/// (<c>JpegDecoderOptions</c>, <c>PngDecoderOptions</c>, etc.) to add format-specific knobs, but the base
/// class itself is directly constructible: when the only thing a caller needs is
/// <see cref="TargetPixelFormat"/> — the common case for a format-agnostic <see cref="Image.Load(Stream, DecoderOptions?)"/>
/// call, where the stream's actual format isn't known up front — construct a plain
/// <c>new DecoderOptions { TargetPixelFormat = ... }</c> rather than guessing at an arbitrary concrete
/// subtype. See <see cref="TargetPixelFormat"/> for the guarantee that makes this safe.
/// </summary>
public class DecoderOptions
{
    /// <summary>
    /// The pixel format the caller wants the decoded <see cref="Image"/> to use, analogous to stb_image's
    /// <c>req_comp</c>. When <see langword="null"/>, the decoder produces whatever format is most natural
    /// for the source data.
    /// </summary>
    /// <remarks>
    /// Every codec's <c>Decode</c> reads this property directly off the base-typed <see cref="DecoderOptions"/>
    /// parameter it's given — never by downcasting to its own concrete options subtype first. (A codec may
    /// separately downcast to its own subtype to read format-specific extras, like JPEG's <c>FastUpsampling</c>,
    /// but that downcast only gates those extras — when it fails because a different concrete type was passed,
    /// the codec falls back to that format's defaults for its own extras and keeps honoring
    /// <see cref="TargetPixelFormat"/> regardless.) Concretely: <c>new DecoderOptions { TargetPixelFormat =
    /// PixelFormat.Rgba32 }</c> passed to <see cref="Image.Load(Stream, DecoderOptions?)"/> is honored no
    /// matter which format the stream turns out to be — a plain <see cref="PeachImage.Formats.Jpeg.JpegDecoderOptions"/>,
    /// <see cref="PeachImage.Formats.Png.PngDecoderOptions"/>, or the base <see cref="DecoderOptions"/> all
    /// behave identically as far as this property is concerned. This is what makes it safe to set
    /// <see cref="TargetPixelFormat"/> once for a format-agnostic decode without picking a per-format subtype
    /// up front.
    /// </remarks>
    public PixelFormat? TargetPixelFormat { get; init; }

    /// <summary>
    /// Called, while the full image is still being decoded, with a lower-fidelity version of it: a JPEG XL preview
    /// frame, one per pass of an interlaced PNG, or one per scan of a progressive JPEG. Formats with no such
    /// intermediate (see <see cref="ImageInfo.HasPreview"/>) never call it.
    /// </summary>
    /// <remarks>
    /// The callback runs synchronously on the decoding thread, zero or more times, before <c>Load</c> returns; the
    /// final image is the return value of <c>Load</c>, not a callback. Each <see cref="Image"/> passed in is a new
    /// copy that the callback owns and must dispose. A preview has the same pixel format as the final image and
    /// may be smaller than it (a JPEG XL preview) or full size with unrefined detail (PNG and JPEG). An exception
    /// thrown from the callback aborts the decode.
    /// </remarks>
    public Action<Image>? PreviewAvailable { get; init; }

    /// <summary>
    /// The names of the formats <see cref="Image.Load(Stream, DecoderOptions?)"/> and <see cref="Image.Identify(Stream, DecoderOptions?)"/>
    /// may accept (<c>"jpeg"</c>, <c>"png"</c>, <c>"gif"</c>, <c>"webp"</c>, ...: the <see cref="ImageFormatInfo.FormatName"/> values of
    /// <see cref="Image.SupportedFormats"/>, compared ignoring case). When <see langword="null"/> (the default) every format is allowed.
    /// </summary>
    /// <remarks>
    /// The check happens right after the format is recognised from the stream's signature, so an image in a format outside the set
    /// throws <see cref="UnsupportedFormatException"/> before any decoder for that format runs; data that matches no format at all
    /// still throws <see cref="UnknownImageFormatException"/>. An empty set allows nothing. A codec chosen explicitly by a
    /// format-selecting options type (raw CCITT, embedded JBIG2) is checked the same way.
    /// </remarks>
    public IReadOnlySet<string>? AllowedFormats { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the decoded <see cref="Image"/> carries no metadata profiles at all (EXIF, XMP, IPTC, ICC, text
    /// and the rest of <see cref="ImageMetadata.Profiles"/>), <see cref="ImageMetadata.HasLocation"/> is <see langword="false"/>, and
    /// the decoder and the location scan do not parse any of it. For a caller that only needs the pixels. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Colour handling that needs the profile (<see cref="Image.ConvertToSrgb()"/>) then has nothing to work from. The embedded ICC profile
    /// is otherwise always returned as the file's raw bytes: its tags are only parsed when <see cref="ImageMetadata.GetIccColorProfile"/>
    /// is called. <see cref="Image.Identify(Stream, DecoderOptions?)"/> skips the location scan but still reports the header-level
    /// <see cref="ImageInfo.Orientation"/>.
    /// </remarks>
    public bool SkipMetadata { get; init; }

    /// <summary>
    /// The most pixels (width x height) an image may declare, checked from the header before any pixel memory is allocated:
    /// a larger image throws <see cref="ImageTooLargeException"/>. When <see langword="null"/> (the default) only each format's own
    /// built-in ceiling applies. This is in addition to, never instead of, those built-in limits.
    /// </summary>
    public long? MaxPixels { get; init; }

    /// <summary>
    /// The most decoded bytes the image may declare per byte of its file, so a few hundred bytes of PNG, GIF or WebP cannot decode into
    /// gigabytes. The ratio is the decoded pixel bytes (width x height x bytes per pixel of the decoded pixel format) divided by the
    /// compressed bytes (the length of the stream from the image's start). Checked from the header, before any pixel memory is allocated;
    /// a larger ratio throws <see cref="ImageTooLargeException"/>. When <see langword="null"/> (the default) there is no ratio limit.
    /// Use <see cref="MaxExpansionRatios"/> to give formats their own limits.
    /// </summary>
    /// <remarks>
    /// <para>Applies to every format. A non-seekable stream is buffered into memory first, since the ratio needs the length.</para>
    /// <para>
    /// The right value depends on the format, which is why one number for everything is a poor fit. Photographic JPEG expands by roughly
    /// 3 to 30 times (4 for CMYK), so a ceiling of 100 to 200 is generous. Lossy WebP and AVIF are similar; a few hundred is safe. Lossless
    /// formats are different: flat graphics compress extremely well, and this library's own encoder writes a flat 400x400 RGBA PNG at about
    /// 5,500 times its size and a flat 2000x2000 one at about 27,000 times, so a PNG, lossless WebP or GIF ceiling needs to be in the tens
    /// of thousands. Combine a tight default with loose entries for the lossless formats through <see cref="MaxExpansionRatios"/>.
    /// </para>
    /// </remarks>
    public double? MaxExpansionRatio { get; init; }

    /// <summary>
    /// Per-format overrides of <see cref="MaxExpansionRatio"/>, keyed by <see cref="ImageFormatInfo.FormatName"/> (<c>"jpeg"</c>, <c>"png"</c>,
    /// <c>"webp"</c>, ..., compared ignoring case). A format with an entry uses it instead of <see cref="MaxExpansionRatio"/>; every other
    /// format falls back to <see cref="MaxExpansionRatio"/>, or is unlimited when that is <see langword="null"/>. The ratio is defined at
    /// <see cref="MaxExpansionRatio"/>. For example <c>new Dictionary&lt;string, double&gt; { ["jpeg"] = 200, ["png"] = 30_000 }</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An entry is not a positive number (checked when the limits are applied).</exception>
    public IReadOnlyDictionary<string, double>? MaxExpansionRatios { get; init; }

    internal bool HasExpansionRatioLimit => MaxExpansionRatio is not null || MaxExpansionRatios is { Count: > 0 };

    /// <summary>The ratio that applies to <paramref name="formatName"/>: its <see cref="MaxExpansionRatios"/> entry, else <see cref="MaxExpansionRatio"/>.</summary>
    internal double? ResolveMaxExpansionRatio(string formatName)
    {
        double? ratio = MaxExpansionRatio;
        if (MaxExpansionRatios is { } perFormat)
        {
            foreach (var entry in perFormat)
            {
                if (string.Equals(entry.Key, formatName, StringComparison.OrdinalIgnoreCase))
                {
                    ratio = entry.Value;
                    break;
                }
            }
        }

        if (ratio is <= 0 or double.NaN)
        {
            throw new ArgumentOutOfRangeException(null, ratio, "The expansion ratio must be a positive number.");
        }

        return ratio;
    }

    /// <summary>
    /// A CMYK profile to colour-manage CMYK pixels with when the image carries no usable profile of its own, for a
    /// <see cref="TargetPixelFormat"/> conversion of a CMYK JPEG or TIFF. <see langword="null"/> (the default) falls back to the profile-less
    /// formula <c>R = 255 x (1 - C) x (1 - K)</c> (likewise G and B), an approximation of how the inks would look.
    /// </summary>
    public IccColorProfile? FallbackCmykProfile { get; init; }

    /// <summary>
    /// Decodes at a reduced size: 1 (the default, full size), 2, 4 or 8 for 1/2, 1/4 or 1/8 of the width and height, rounded up
    /// (<c>ceil(size / N)</c>). JPEG, JPEG 2000 and JPEG XL reduce natively, which saves most of the decode work and memory (JPEG scales in
    /// the DCT domain, so a 24 megapixel photo at 1/8 never builds a full-size image); every other format decodes at full size and is then
    /// averaged down, so the result size is the same but nothing is saved. <see cref="TargetSize"/>, when set, chooses the denominator instead.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxPixels"/> and <see cref="MaxExpansionRatio"/> are checked against the size the header declares, not the reduced size.
    /// <see cref="ImageInfo"/> from <see cref="Image.Identify(Stream, DecoderOptions?)"/> also reports the declared size. A progressive JPEG
    /// reduces the same way as a baseline one, and the progressive previews (<see cref="PreviewAvailable"/>) arrive at the reduced size. The
    /// reduced image is a faithful lower-resolution rendition, but not bit-identical to resizing the full decode (DCT-domain reduction is
    /// close to a box filter), so expect small differences. The metadata and EXIF orientation are those of the file; see <see cref="ApplyOrientation"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 1, 2, 4 or 8 (reported when the image is loaded).</exception>
    public int ScaleDenominator { get; init; } = 1;

    /// <summary>
    /// The size the caller is going to resize to. When set, the decoder reduces by the largest of 1/2, 1/4 and 1/8 that still leaves an
    /// image at least this wide and this tall (so a 6000x4000 JPEG with a target of 600x420 decodes at 750x500, 1/8). Decoding then costs
    /// a fraction of a full decode and a final <see cref="Image.Resize(int, int, ResizeOptions?)"/> to the target finishes the job. When
    /// <see langword="null"/> (the default) <see cref="ScaleDenominator"/> applies. With <see cref="ApplyOrientation"/> set, the target
    /// is compared against the upright (displayed) size, otherwise against the stored size. See <see cref="ScaleDenominator"/> for
    /// which formats reduce natively and how it interacts with the limits.
    /// </summary>
    public (int Width, int Height)? TargetSize { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the decoded <see cref="Image"/> comes back upright: the EXIF orientation recorded in the file is applied
    /// to the pixels and reset in the image EXIF profile, as <see cref="Image.ApplyOrientation(ImageOrientation)"/> does. Defaults to
    /// <see langword="false"/>, which leaves the pixels as stored (decoders never apply orientation by themselves).
    /// </summary>
    /// <remarks>
    /// Together with <see cref="ScaleDenominator"/> or <see cref="TargetSize"/> this saves work: the rotation or flip runs once on the already
    /// reduced image instead of on a full-size decode, and the caller needs no second copy. Without scaling it only saves the caller
    /// the extra call. The orientation comes from the header, as <see cref="ImageInfo.Orientation"/> reports it.
    /// </remarks>
    public bool ApplyOrientation { get; init; }

    // Set on a private copy by Image.Load once it has resolved the scale from the header and orientation, so a codec uses that value.
    internal int ResolvedScaleDenominator { get; private set; }

    internal DecoderOptions WithResolvedScaleDenominator(int denominator)
    {
        var clone = (DecoderOptions)MemberwiseClone();
        clone.ResolvedScaleDenominator = denominator;
        return clone;
    }

    /// <summary>
    /// The most threads one decode call may use. -1 (the default) leaves it to the library, which fans out over up to
    /// <see cref="Environment.ProcessorCount"/> workers; 1 decodes sequentially on the calling thread. The limit is per call, not
    /// global: concurrent calls each use up to this many, so a server decoding uploads should set a low value and/or share a
    /// <see cref="TaskScheduler"/>. Does not change the output.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = -1;

    /// <summary>
    /// An optional scheduler the decoder's parallel work runs on, so a host can share one limit across concurrent calls (for example
    /// the <see cref="ConcurrentExclusiveSchedulerPair.ConcurrentScheduler"/> of a pair created with a bounded concurrency level).
    /// <see langword="null"/> (the default) uses the thread pool.
    /// </summary>
    public TaskScheduler? TaskScheduler { get; init; }
}
