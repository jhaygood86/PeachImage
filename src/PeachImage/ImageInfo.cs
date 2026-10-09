namespace PeachImage;

/// <summary>Lightweight image dimensions/format info obtainable without decoding full pixel data.</summary>
/// <param name="Width">The image width in pixels.</param>
/// <param name="Height">The image height in pixels.</param>
/// <param name="PixelFormat">The pixel format a full decode would produce.</param>
/// <param name="FormatName">The name of the format that produced this info, e.g. <c>"jpeg"</c>.</param>
/// <param name="IsAnimated">
/// Whether the source is a multi-frame animation. For an animated source, <see cref="Image.Load(Stream, DecoderOptions?)"/>
/// would decode only its first frame (see <see cref="Image.IsAnimated"/>) — use <see cref="AnimatedImage"/> to
/// decode every frame.
/// </param>
/// <param name="HasAlpha">Whether the source actually carries an alpha channel — see <see cref="Image.HasAlpha"/>.</param>
/// <param name="IsAdobeInvertedCmyk">
/// JPEG/Adobe-specific: whether a direct-CMYK source stores its samples in Adobe's inverted convention
/// (<c>255-x</c>). Always <see langword="false"/> for non-JPEG formats and non-CMYK JPEGs. Relevant to
/// <see cref="Formats.Jpeg.JpegDecoderOptions.KeepAdobeCmykInverted"/> — by default, a full decode de-inverts
/// these bytes back to the standard CMYK convention regardless of this flag.
/// </param>
/// <param name="IsYcck">
/// JPEG/Adobe-specific: whether a 4-component source is really YCCK under the hood (Adobe APP14 transform=2)
/// rather than direct CMYK. Always <see langword="false"/> for non-JPEG formats and non-YCCK JPEGs. Relevant
/// to <see cref="Formats.Jpeg.JpegDecoderOptions.DecodeRawYcck"/> — by default, a full decode converts YCCK to
/// CMYK regardless of this flag.
/// </param>
/// <param name="IsLosslessEncoding">
/// Whether the source's pixel data was encoded losslessly by its original codec, so a caller can avoid
/// silently re-encoding an already-lossy source under a full-fidelity assumption. Semantics are
/// format-specific:
/// <list type="bullet">
/// <item>WebP: <see langword="true"/> for a VP8L (lossless) bitstream, <see langword="false"/> for VP8
/// (lossy). Always <see langword="false"/> for an animated WebP — lossy/lossless is genuinely a
/// per-frame (<c>ANMF</c>) property there, so a single file-level answer isn't well-defined.</item>
/// <item>AVIF: <see langword="true"/> only when every AV1 tile's frame header reports the coded stream
/// as fully lossless (identity-transform, qindex/delta-q all zero, and no superres upscaling) —
/// AND'd across every tile of a HEIF grid composite. A malformed AV1 tile bitstream degrades this to
/// <see langword="false"/> rather than throwing, matching <see cref="Image.Identify(Stream)"/>'s
/// "header-level info only" leniency elsewhere.</item>
/// <item>TIFF: <see langword="true"/> when the Compression tag is 1 (none), 5 (LZW), or 32773
/// (PackBits) — this decoder's entire supported compression set, which is lossless by construction, so
/// this is always <see langword="true"/> for any TIFF that decodes successfully today.</item>
/// <item>JPEG XL: <see langword="true"/> when the colour channels are stored in the original colour space
/// (not XYB) and the first frame is Modular, i.e. a lossless encode. <see langword="false"/> for lossy
/// (XYB) files, for JPEG reconstruction (VarDCT frames, lossless only with respect to the original JPEG
/// bytes) and for images with a preview.</item>
/// <item>Always <see langword="false"/> for every other format, including JPEG (always lossy) and PNG/BMP/GIF
/// (not format-specific here since they have no lossy mode at all — see the general opaque-source handling
/// elsewhere rather than this flag).</item>
/// </list>
/// </param>
public readonly record struct ImageInfo(
    int Width,
    int Height,
    PixelFormat PixelFormat,
    string FormatName,
    bool IsAnimated = false,
    bool HasAlpha = false,
    bool IsAdobeInvertedCmyk = false,
    bool IsYcck = false,
    bool IsLosslessEncoding = false);
