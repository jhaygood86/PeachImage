using PeachImage.Formats.Jpeg.Markers.Segments;

namespace PeachImage.Formats.Jpeg.Decoding.ColorSpace;

/// <summary>Resolves a frame's <see cref="JpegColorSpace"/> and CMYK inversion convention from its component count and any Adobe APP14 marker.</summary>
internal static class ColorSpaceResolver
{
    /// <summary>
    /// Determines the color space samples are encoded in, and whether 4-component samples follow Adobe's
    /// inverted-CMYK convention (ink stored as <c>255-x</c>, so 255 means no ink). Adobe writes both direct CMYK
    /// (transform=0) and YCCK (transform=2) that way. <see cref="ColorConversion.IColorConverter.YcckToCmyk"/> only
    /// undoes the YCbCr transform; it still returns the inverted ink, so YCCK needs the same de-inversion as transform=0.
    /// </summary>
    public static (JpegColorSpace ColorSpace, bool IsAdobeInverted) Resolve(int componentCount, JpegAdobeSegment? adobe) =>
        componentCount switch
        {
            1 => (JpegColorSpace.Grayscale, false),
            3 => adobe switch
            {
                null => (JpegColorSpace.YCbCr, false),
                { Transform: 0 } => (JpegColorSpace.Rgb, false),
                { Transform: 1 } => (JpegColorSpace.YCbCr, false),
                { Transform: var transform } => throw new JpegDecodingException($"Unsupported Adobe APP14 transform {transform} for a 3-component JPEG (expected 0 or 1)."),
            },
            4 => adobe switch
            {
                null => (JpegColorSpace.Cmyk, false),
                { Transform: 0 } => (JpegColorSpace.Cmyk, true),
                { Transform: 2 } => (JpegColorSpace.Ycck, true),
                { Transform: var transform } => throw new JpegDecodingException($"Unsupported Adobe APP14 transform {transform} for a 4-component JPEG (expected 0 or 2)."),
            },
            _ => throw new JpegDecodingException($"Unsupported JPEG component count: {componentCount}."),
        };
}
