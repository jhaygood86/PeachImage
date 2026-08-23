namespace PeachImage.Formats.Tiff.Internal;

/// <summary>Sanity-check limits applied while decoding, to reject hostile/corrupt input before large allocations. Mirrors Bmp's/Jpeg's DecodingLimits.</summary>
internal static class TiffDecodingLimits
{
    /// <summary>The largest pixel count (width * height) a decode will attempt to allocate.</summary>
    public const long MaxPixelCount = 268_435_456;

    /// <summary>
    /// The largest single strip's compressed (or uncompressed) byte count a decode will attempt to allocate
    /// before reading it. Same order of magnitude as <see cref="MaxPixelCount"/> — a strip belonging to a
    /// canvas already bounded by that pixel-count limit has no legitimate reason to exceed it. Without this,
    /// a tiny file could declare an arbitrary StripByteCounts entry and force that allocation immediately,
    /// regardless of how much data the stream actually contains.
    /// </summary>
    public const long MaxDeclaredStripByteCount = 268_435_456;

    /// <summary>The largest number of IFD tag entries a single IFD will attempt to read, guarding against a hostile declared entry count driving an unbounded read loop.</summary>
    public const int MaxIfdEntryCount = 65_535;

    /// <summary>
    /// The largest input file size, in bytes, a decode will attempt to buffer into memory. TIFF tag values
    /// and strip data are addressed by absolute file offset and jump around arbitrarily, so the whole file
    /// must be buffered up front rather than streamed — see <c>TiffContainerReader</c>. Mirrors Avif's own
    /// <c>MaxFileSize</c> for the same reason.
    /// </summary>
    public const long MaxFileSize = 512L * 1024 * 1024;

    /// <summary>The largest number of values an IFD entry's array (StripOffsets, StripByteCounts, BitsPerSample, ColorMap, ...) will be allocated for, guarding against a hostile declared Count driving a huge allocation before the entry's actual meaning (and therefore a tighter, tag-specific bound) is even known.</summary>
    public const int MaxArrayEntryCount = 4_000_000;
}
