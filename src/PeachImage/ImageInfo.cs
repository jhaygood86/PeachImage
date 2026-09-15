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
public readonly record struct ImageInfo(
    int Width,
    int Height,
    PixelFormat PixelFormat,
    string FormatName,
    bool IsAnimated = false,
    bool HasAlpha = false,
    bool IsAdobeInvertedCmyk = false,
    bool IsYcck = false);
