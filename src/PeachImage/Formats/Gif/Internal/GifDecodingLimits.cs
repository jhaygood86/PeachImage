namespace PeachImage.Formats.Gif.Internal;

/// <summary>Sanity-check limits applied while decoding, to reject hostile/corrupt input before large allocations. Mirrors Bmp's/Jpeg's DecodingLimits.</summary>
internal static class GifDecodingLimits
{
    /// <summary>The largest pixel count (width * height) a decode will attempt to allocate for the logical canvas or any single frame.</summary>
    public const long MaxPixelCount = 268_435_456;

    /// <summary>The most frames <see cref="GifDecoder.DecodeAnimation"/> will decode from a single stream before giving up (guards against a maliciously/corruptly unbounded frame stream).</summary>
    public const int MaxFrameCount = 100_000;

    /// <summary>The most bytes any single Comment/Plain Text/Application extension's sub-blocks will be read into before giving up.</summary>
    public const int MaxExtensionBytes = 16 * 1024 * 1024;

    /// <summary>The most bytes a single frame's LZW-compressed image-data sub-blocks will be read into before giving up.</summary>
    public const long MaxImageDataBytes = 512L * 1024 * 1024;

    /// <summary>
    /// The most total bytes <see cref="GifDecoder.DecodeAnimation"/> will allocate across every frame's
    /// full-canvas RGBA snapshot combined. Each decoded frame is composited onto — and then copied out of —
    /// the full logical-screen canvas regardless of that frame's own (possibly tiny) declared size, and every
    /// frame's copy is retained until the whole animation has been decoded. Without a joint limit, neither
    /// <see cref="MaxPixelCount"/> (bounds one canvas) nor <see cref="MaxFrameCount"/> (bounds the frame
    /// count) alone stops a modest canvas combined with tens of thousands of frames from multiplying into an
    /// enormous total allocation from a tiny file.
    /// </summary>
    public const long MaxCumulativeCanvasBytes = 1_073_741_824;
}
