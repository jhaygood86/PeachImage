namespace PeachImage.Formats.Jxl.Internal;

/// <summary>Sanity-check limits applied while decoding, to reject hostile/corrupt input before large allocations. Mirrors the Tiff and Avif decoding limits.</summary>
internal static class JxlDecodingLimits
{
    /// <summary>The largest pixel count (width * height) a decode will attempt to allocate.</summary>
    public const long MaxPixelCount = 268_435_456;

    /// <summary>The largest input file size, in bytes, a decode will attempt to buffer into memory.</summary>
    public const long MaxFileSize = 512L * 1024 * 1024;

    /// <summary>The largest decompressed size of a Brotli-compressed (<c>brob</c>) metadata box.</summary>
    public const int MaxMetadataBoxSize = 64 * 1024 * 1024;

    /// <summary>The most extra channels a codestream may declare (the format's own limit is 4096 via U32(BitsOffset(12, 1))).</summary>
    public const int MaxExtraChannels = 4096;

    /// <summary>The longest name, in bytes, a header may declare.</summary>
    public const int MaxNameLength = 1072;
}
