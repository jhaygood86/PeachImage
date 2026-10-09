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
}
