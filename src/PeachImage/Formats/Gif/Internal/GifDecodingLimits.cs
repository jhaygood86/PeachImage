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
    /// The cumulative frame-canvas bytes <see cref="GifDecoder.DecodeAnimation"/> will allow across however
    /// many frames have been decoded so far. <see cref="AnimatedImage.Frames"/> is lazy — frames aren't all
    /// retained simultaneously, so this no longer bounds *retained* memory the way it once did when every
    /// frame's full-canvas copy was kept resident for the whole animation. It's still a meaningful guard
    /// against decode-time CPU/GC pressure from a single pathological file (a modest canvas combined with an
    /// enormous frame count still costs real work per frame, even though nothing is being multiplied and
    /// retained anymore), so the cap and its enforcement are kept.
    /// </summary>
    public const long MaxCumulativeCanvasBytes = 1_073_741_824;

    /// <summary>
    /// The largest logical-screen canvas <see cref="GifDecoder.Decode"/> (single-frame decode) will allocate,
    /// in bytes, worst-case (RGBA32, 4 bytes/pixel). GifSingleFrameDecoder allocates at the logical-screen's
    /// declared dimensions regardless of the actual first frame's (possibly tiny) size or the stream's actual
    /// compressed data length, so a file of only a few dozen bytes can otherwise force an allocation up to
    /// <see cref="MaxPixelCount"/> x 4 bytes (~1GB). Deliberately lower than that general per-canvas ceiling —
    /// a single-frame decode has no legitimate need for a canvas anywhere near the multi-frame-animation limit.
    /// </summary>
    public const long MaxInitialCanvasBytes = 268_435_456;
}
