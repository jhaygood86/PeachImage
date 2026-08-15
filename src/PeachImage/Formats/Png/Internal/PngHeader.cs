using PeachImage.Formats.Png;

namespace PeachImage.Formats.Png.Internal;

/// <summary>Parsed, validated IHDR contents plus derived layout helpers used throughout the codec.</summary>
internal readonly record struct PngHeader(
    int Width,
    int Height,
    byte BitDepth,
    PngColorType ColorType,
    byte CompressionMethod,
    byte FilterMethod,
    byte InterlaceMethod)
{
    /// <summary>Number of samples (channel values) per pixel for this header's color type.</summary>
    public int SamplesPerPixel => ColorType switch
    {
        PngColorType.Grayscale => 1,
        PngColorType.Truecolor => 3,
        PngColorType.Palette => 1,
        PngColorType.GrayscaleAlpha => 2,
        PngColorType.TruecolorAlpha => 4,
        _ => throw new PngDecodingException($"Unsupported PNG color type {(byte)ColorType}."),
    };

    /// <summary>Total bits occupied by one pixel (samples-per-pixel × bit depth).</summary>
    public int BitsPerPixel => SamplesPerPixel * BitDepth;

    /// <summary>
    /// The filter "distance back" in bytes (spec §6.3): the byte offset a Sub/Average/Paeth filter looks
    /// back to find the corresponding sample in the same row. Always at least 1.
    /// </summary>
    public int FilterBytesPerPixel => Math.Max(1, (BitsPerPixel + 7) / 8);

    public bool IsInterlaced => InterlaceMethod == 1;

    /// <summary>Bytes needed for one packed (still bit-depth-packed) scanline of the given pixel width, excluding the leading filter-type byte.</summary>
    public static int BytesPerScanline(int pixelWidth, int bitsPerPixel) =>
        (int)(((long)pixelWidth * bitsPerPixel + 7) / 8);
}
