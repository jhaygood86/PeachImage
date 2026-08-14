using PeachImage.Formats.Jpeg.Markers.Segments;

namespace PeachImage.Formats.Jpeg.Decoding.ColorSpace;

/// <summary>Resolves a frame's <see cref="JpegColorSpace"/> and CMYK inversion convention from its component count and any Adobe APP14 marker.</summary>
internal static class ColorSpaceResolver
{
    /// <summary>
    /// Determines the color space samples are encoded in, and whether direct (non-YCCK) 4-component samples
    /// follow Adobe's inverted-CMYK convention (values stored as <c>255-x</c>). For YCCK, the necessary
    /// inversion is inherent in the YCbCr-&gt;CMY transform itself (see <see cref="ColorConversion.IColorConverter.YcckToCmyk"/>),
    /// so <c>IsAdobeInverted</c> is only meaningful for the direct-CMYK (transform=0) case.
    /// </summary>
    public static (JpegColorSpace ColorSpace, bool IsAdobeInverted) Resolve(int componentCount, JpegAdobeSegment? adobe) =>
        componentCount switch
        {
            1 => (JpegColorSpace.Grayscale, false),
            3 => adobe is { Transform: 0 }
                ? (JpegColorSpace.Rgb, false)
                : (JpegColorSpace.YCbCr, false),
            4 => adobe is { Transform: 2 }
                ? (JpegColorSpace.Ycck, false)
                : (JpegColorSpace.Cmyk, adobe.HasValue),
            _ => throw new JpegDecodingException($"Unsupported JPEG component count: {componentCount}."),
        };
}
