namespace PeachImage.Formats.Jxl;

/// <summary>
/// JPEG XL-specific encode options. Supported sources are 8- and 16-bit gray, RGB and RGBA
/// (<see cref="PixelFormat.Gray8"/>, <see cref="PixelFormat.Rgb24"/>, <see cref="PixelFormat.Rgba32"/> and their
/// 16-bit counterparts) and the 32-bit float formats (<see cref="PixelFormat.GrayF32"/>,
/// <see cref="PixelFormat.RgbF32"/>, <see cref="PixelFormat.RgbaF32"/>, sRGB-encoded, values above 1 allowed);
/// a fully opaque RGBA image is stored without an alpha channel. Float samples are stored bit for bit when
/// <see cref="Lossless"/> is set (lossless compression of float data is poor, since the samples are coded as integers). Alpha is straight
/// (not premultiplied). The color space is signalled as sRGB (or gray with the sRGB transfer function), and any
/// ICC, Exif or XMP profiles on the image are stored alongside when <see cref="EncoderOptions.IncludeMetadata"/> is set.
/// </summary>
public sealed class JxlEncoderOptions : EncoderOptions
{
    /// <summary>
    /// When <see langword="true"/>, encodes with the Modular coder so every sample is reproduced exactly, using a
    /// reversible YCoCg color transform, gradient prediction and rANS-coded residuals. Defaults to
    /// <see langword="false"/>, in which case <see cref="Quality"/> selects lossy encoding.
    /// </summary>
    public bool Lossless { get; init; }

    /// <summary>
    /// Quality, 0 (worst/smallest) to 100 (best/largest). Defaults to 75. Ignored when <see cref="Lossless"/> is
    /// <see langword="true"/>.
    /// </summary>
    public int Quality { get; init; } = 75;

    /// <summary>
    /// How hard lossy encoding looks for a smaller file, 1 (fastest) to 9 (slowest). Efforts 1 to 3 use 8x8 blocks only, 4 to 6
    /// also try 16x16 and 16x8/8x16 blocks, and 7 to 9 add 32x32 and the 32-wide rectangles. Defaults to 5. Has no effect on
    /// lossless encoding.
    /// </summary>
    public int Effort { get; init; } = 5;

    /// <summary>
    /// When <see langword="true"/>, lossy encoding quantizes smooth blocks more finely and busy blocks more coarsely at the same
    /// average rate, the way libjxl's adaptive quantization does. Defaults to <see langword="false"/>: on a set of photographs it
    /// made files 3 to 8 percent larger at the same SSIMULACRA2 score (and lowered PSNR), so it only makes sense where the
    /// viewer's masking of error by texture matters more than those measures capture. Ignored when <see cref="Lossless"/> is <see langword="true"/>.
    /// </summary>
    public bool AdaptiveQuantization { get; init; }
}
