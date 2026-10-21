namespace PeachImage.Formats.Jpeg2000.Internal;

/// <summary>Sanity-check limits applied while decoding, to reject hostile/corrupt input before large allocations.</summary>
internal static class Jpeg2000DecodingLimits
{
    /// <summary>The largest pixel count (width * height) a decode will attempt to allocate per component.</summary>
    public const long MaxPixelCount = 268_435_456;

    /// <summary>The largest input file size, in bytes, a decode will buffer.</summary>
    public const long MaxFileSize = 512L * 1024 * 1024;

    /// <summary>The most components a codestream may declare.</summary>
    public const int MaxComponents = 16384;

    /// <summary>The most tiles a codestream may declare.</summary>
    public const int MaxTiles = 65535;

    /// <summary>The most code-blocks a single tile may hold.</summary>
    public const long MaxCodeBlocksPerTile = 16_777_216;
}
