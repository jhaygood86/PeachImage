namespace PeachImage.Formats.Png;

/// <summary>
/// The result of splitting a PNG's per-pixel alpha channel out into an independent plane. See
/// <see cref="PngAlphaSplit.TrySplit(in PngPassthroughInfo, out PngAlphaSplitInfo)"/>.
/// </summary>
/// <param name="Width">The image width in pixels.</param>
/// <param name="Height">The image height in pixels.</param>
/// <param name="AlphaBitDepth">The bit depth of each <see cref="AlphaData"/> sample. Always 8 in the current implementation.</param>
/// <param name="AlphaData">
/// A complete, independent zlib (RFC 1950) stream of single-channel (DeviceGray) alpha samples,
/// <b>PNG-filtered exactly like a PNG <c>IDAT</c> stream</b> — every row is prefixed with a filter-type
/// byte (always 0/None here) followed by <see cref="Width"/> sample bytes, the same row shape a real PNG
/// file's own <c>IDAT</c> uses. This is deliberate, not raw flat pixel bytes: it lets a caller embed this
/// stream as a PDF <c>/FlateDecode</c> <c>/SMask</c> with <c>/DecodeParms &lt;&lt; /Predictor 15 /Colors 1
/// /BitsPerComponent 8 /Columns <see cref="Width"/> &gt;&gt;</c> — PDF's own "PNG prediction" mechanism,
/// which a compliant PDF reader decodes identically to a real PNG row filter — the same technique already
/// applies to <see cref="PngPassthroughInfo.IdatData"/> itself (which keeps its original filter bytes
/// verbatim), so this keeps both halves of a split image consistent with how PNG-sourced PDF image data
/// is always framed.
/// </param>
/// <param name="ColorData">
/// A complete, independent zlib (RFC 1950) stream of the de-interleaved color samples (gray or RGB, per
/// <see cref="ColorIsRgb"/>), framed exactly like <see cref="AlphaData"/> (a leading filter-type-None byte
/// per row) — ready to embed as a PDF <c>/FlateDecode</c> color XObject stream with the matching
/// <c>/DecodeParms /Predictor 15</c> (<c>/Colors</c> 1 or 3 per <see cref="ColorIsRgb"/>).
/// <see langword="null"/> when the source was a palette (indexed) image: in that case the color side needs
/// no new data at all — pair the original <see cref="PngPassthroughInfo.IdatData"/> and
/// <see cref="PngPassthroughInfo.PaletteData"/> with a PDF <c>/Indexed</c> color space as usual, and use
/// only <see cref="AlphaData"/> from this result as the <c>/SMask</c>.
/// </param>
/// <param name="ColorBitDepth">The bit depth of each <see cref="ColorData"/> sample, or 0 when <see cref="ColorData"/> is <see langword="null"/>.</param>
/// <param name="ColorIsRgb">
/// Whether <see cref="ColorData"/> holds 3-sample RGB pixels (source color type 6, TruecolorAlpha) as
/// opposed to 1-sample grayscale pixels (source color type 4, GrayscaleAlpha). Meaningless when
/// <see cref="ColorData"/> is <see langword="null"/>.
/// </param>
public readonly record struct PngAlphaSplitInfo(
    int Width,
    int Height,
    byte AlphaBitDepth,
    byte[] AlphaData,
    byte[]? ColorData,
    byte ColorBitDepth,
    bool ColorIsRgb);
