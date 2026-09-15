namespace PeachImage.Formats.Jpeg;

/// <summary>JPEG-specific decode options.</summary>
public sealed class JpegDecoderOptions : DecoderOptions
{
    /// <summary>
    /// Use fast nearest-neighbor chroma upsampling instead of the smoother (but slightly more expensive)
    /// default triangle-filter upsampling.
    /// </summary>
    public bool FastUpsampling { get; init; }

    /// <summary>
    /// When <see langword="true"/>, a direct-CMYK source stored in Adobe's inverted convention
    /// (<see cref="ImageInfo.IsAdobeInvertedCmyk"/>) is returned exactly as stored (still
    /// <see cref="PixelFormat.Cmyk32"/>), instead of the default behavior of de-inverting it back to the
    /// standard CMYK convention. Has no effect on non-CMYK or non-inverted sources. Combining this with a
    /// <see cref="DecoderOptions.TargetPixelFormat"/> other than <see cref="PixelFormat.Cmyk32"/> is a
    /// contradiction — asking to keep raw inverted bytes while also asking to convert away from CMYK — and
    /// causes <see cref="JpegDecodingException"/> to be thrown before any decoding happens.
    /// </summary>
    public bool KeepAdobeCmykInverted { get; init; }

    /// <summary>
    /// When <see langword="true"/> and the source is really YCCK under the hood
    /// (<see cref="ImageInfo.IsYcck"/>), the four raw, interleaved, unconverted planes are returned as
    /// <see cref="PixelFormat.Ycck32"/> instead of the default behavior of converting them to
    /// <see cref="PixelFormat.Cmyk32"/>. Has no effect when the source isn't YCCK.
    /// </summary>
    public bool DecodeRawYcck { get; init; }
}
