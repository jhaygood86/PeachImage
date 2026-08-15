namespace PeachImage.Formats.Png.Internal;

/// <summary>Sanity-check limits applied while decoding, to reject hostile/corrupt input before large allocations. Mirrors Bmp/Jpeg's DecodingLimits.</summary>
internal static class PngDecodingLimits
{
    /// <summary>The largest pixel count (width * height) a decode will attempt to allocate.</summary>
    public const long MaxPixelCount = 268_435_456;

    /// <summary>The PNG spec's own hard ceiling on a single chunk's data length (2^31 - 1).</summary>
    public const long MaxChunkDataLength = 2_147_483_591;

    /// <summary>Cap on a single ancillary chunk's byte size, independent of image geometry (ancillary chunks have no geometry-derived expected size the way IDAT does).</summary>
    public const long MaxAncillaryChunkBytes = 67_108_864;

    /// <summary>
    /// Cap on the *decompressed* output of an ancillary chunk's deflate stream (iCCP/zTXt/iTXt). Unlike IDAT,
    /// whose decompressed size is bounded by image geometry validated before decompression even starts, an
    /// ancillary chunk's inflated size has no geometry-derived bound — without this cap, a small
    /// <see cref="MaxAncillaryChunkBytes"/>-sized chunk of highly compressible data could inflate to a
    /// classic deflate-bomb-scale allocation.
    /// </summary>
    public const long MaxInflatedAncillaryBytes = 67_108_864;
}
