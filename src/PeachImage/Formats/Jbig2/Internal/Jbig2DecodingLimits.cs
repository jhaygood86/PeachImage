namespace PeachImage.Formats.Jbig2.Internal;

/// <summary>Sanity-check limits applied while decoding, to reject hostile/corrupt input before large allocations.</summary>
internal static class Jbig2DecodingLimits
{
    /// <summary>The largest pixel count (width * height) a bitmap may have.</summary>
    public const long MaxPixelCount = 268_435_456;

    /// <summary>The largest input size, in bytes, a decode will buffer.</summary>
    public const long MaxFileSize = 512L * 1024 * 1024;

    /// <summary>The most symbols a dictionary may declare.</summary>
    public const int MaxSymbols = 1 << 22;

    /// <summary>The most segments a stream may hold.</summary>
    public const int MaxSegments = 1 << 20;

    /// <summary>The most instances a text region may declare.</summary>
    public const int MaxInstances = 1 << 26;
}
