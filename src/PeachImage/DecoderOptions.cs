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
    /// Colour handling that needs the profile (<see cref="Image.ConvertToSrgb"/>) then has nothing to work from. The embedded ICC profile
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
    /// The most decoded bytes the image may declare per byte of its file (width x height x bytes per pixel, divided by the length of the
    /// stream from the image's start), so a few hundred bytes of PNG, GIF or WebP cannot decode into gigabytes. Checked from the
    /// header, before decoding; a larger ratio throws <see cref="ImageTooLargeException"/>. When <see langword="null"/> (the default)
    /// there is no ratio limit. Genuine images expand by roughly 1 to 50 times (flat graphics can reach a few hundred).
    /// </summary>
    /// <remarks>Applies to every format. A non-seekable stream is buffered into memory first, since the ratio needs the length.</remarks>
    public double? MaxExpansionRatio { get; init; }
}
