namespace PeachImage.Formats.Jpeg2000;

/// <summary>
/// JPEG 2000-specific decode options. Raw codestreams and JP2 files are recognized by their signatures, so ordinary loads
/// need none; these options matter mostly to PDF <c>JPXDecode</c> streams, whose colour space comes from the PDF, not the image.
/// </summary>
public sealed class Jpeg2000DecoderOptions : DecoderOptions
{
    /// <summary>
    /// Ignore the JP2 colour specification, palette and channel definition boxes (and any sYCC to RGB conversion): the
    /// codestream's components are returned as they are, one to <see cref="PixelFormat.Gray8"/>, two to gray plus alpha
    /// in <see cref="PixelFormat.Rgba32"/>, three to <see cref="PixelFormat.Rgb24"/> and four to <see cref="PixelFormat.Cmyk32"/>
    /// (or the 16-bit equivalents for deeper samples where one exists). Default false.
    /// </summary>
    public bool IgnoreColorSpace { get; init; }

    /// <summary>
    /// Decodes at a reduced resolution: each increment halves the width and height (a value of 1 gives half size). The
    /// value is clamped to the number of decomposition levels the image has. Default 0 (full size).
    /// </summary>
    public int ReduceResolutionLevels { get; init; }
}
