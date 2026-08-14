namespace PeachImage.Formats.Jpeg.Internal;

/// <summary>
/// Safety limits enforced during decode to reject pathological or malicious inputs (e.g. a tiny file
/// claiming an enormous frame size) before they can trigger huge allocations or integer overflow in
/// block-count arithmetic, rather than letting such files crash the process.
/// </summary>
internal static class JpegDecodingLimits
{
    /// <summary>
    /// The largest total pixel count (width * height) a frame may declare. 2^28 (~268 megapixels) comfortably
    /// covers real-world photos (including large scans/panoramas) while keeping the resulting coefficient
    /// buffer allocations bounded to a safe, predictable size.
    /// </summary>
    public const long MaxPixelCount = 268_435_456;
}
