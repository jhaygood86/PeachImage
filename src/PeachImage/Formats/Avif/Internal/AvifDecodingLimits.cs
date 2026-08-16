namespace PeachImage.Formats.Avif.Internal;

/// <summary>Sanity-check limits applied while parsing/decoding AVIF, to reject hostile/corrupt input before large allocations. Mirrors Webp's <c>WebpDecodingLimits</c>.</summary>
internal static class AvifDecodingLimits
{
    /// <summary>The largest single canvas dimension a decode will attempt to allocate for.</summary>
    public const int MaxDimension = 16_384;

    /// <summary>The largest pixel count (width * height) a decode will attempt to allocate for. Equals <see cref="MaxDimension"/> squared, matching the other formats' MaxPixelCount for consistency.</summary>
    public const long MaxPixelCount = 268_435_456;

    /// <summary>The largest input file size, in bytes, a decode will attempt to buffer into memory. AVIF's <c>iloc</c> item extents are addressed by absolute offset, so the whole file must be buffered up front rather than streamed — see <c>AvifContainerReader</c>.</summary>
    public const long MaxFileSize = 512L * 1024 * 1024;

    /// <summary>The largest single item's data (summed across all its <c>iloc</c> extents) a decode will attempt to allocate/copy. An item can never legitimately exceed the file that contains it.</summary>
    public const long MaxItemDataLength = MaxFileSize;

    /// <summary>The deepest ISOBMFF box nesting a container parse will follow before giving up, guarding against a hostile self-referential/deeply-nested box tree.</summary>
    public const int MaxBoxNestingDepth = 12;

    /// <summary>Sanity cap on the number of items an <c>iinf</c>/<c>iloc</c> box may declare in a single file.</summary>
    public const int MaxItemCount = 4_096;

    /// <summary>Sanity cap on the number of tiles a <c>grid</c> derived image item may declare.</summary>
    public const int MaxGridTiles = 256;
}
