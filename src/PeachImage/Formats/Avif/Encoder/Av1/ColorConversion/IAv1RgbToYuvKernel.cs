namespace PeachImage.Formats.Avif.Encoder.Av1.ColorConversion;

/// <summary>
/// Computes the pixel-parallel stage of <see cref="Av1RgbToYuvConverter"/>'s RGB24-&gt;YUV conversion --
/// full-resolution BT.601 luma/chroma, before the 4:2:0 chroma box-filter downsample (which stays a plain
/// scalar loop over the much smaller chroma-resolution grid; see <see cref="Av1RgbToYuvConverter"/>'s own
/// remarks for why that split is the right one).
/// </summary>
internal interface IAv1RgbToYuvKernel
{
    /// <summary>Widens <paramref name="pixelCount"/> Gray8 samples into <paramref name="yOut"/> verbatim (a true gray sample's BT.601 Y projection is itself).</summary>
    void ConvertMonoChrome(ReadOnlySpan<byte> gray, Span<int> yOut, int pixelCount);

    /// <summary>
    /// Converts <paramref name="pixelCount"/> packed RGB24 pixels into full-resolution Y (rounded, clamped
    /// to [0, 255], as the entropy-ready sample) and full-resolution Cb/Cr (left as raw, unclamped floats --
    /// clamping happens once, after <see cref="Av1RgbToYuvConverter"/> box-filter-averages four of them down
    /// to one 4:2:0 chroma sample, exactly mirroring the original double-precision implementation's order of
    /// operations).
    /// </summary>
    void RgbToYuvFullRes(ReadOnlySpan<byte> rgb, Span<int> yOut, Span<float> cbFullOut, Span<float> crFullOut, int pixelCount);
}
