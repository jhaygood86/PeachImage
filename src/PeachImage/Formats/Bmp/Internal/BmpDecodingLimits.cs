namespace PeachImage.Formats.Bmp.Internal;

/// <summary>Sanity-check limits applied while decoding, to reject hostile/corrupt input before large allocations. Mirrors Jpeg's JpegDecodingLimits.</summary>
internal static class BmpDecodingLimits
{
    /// <summary>The largest pixel count (width * height) a decode will attempt to allocate.</summary>
    public const long MaxPixelCount = 268_435_456;

    /// <summary>
    /// The largest RLE-compressed image data size (BITMAPINFOHEADER's declared biSizeImage) a decode will
    /// attempt to allocate before reading a single byte. Same order of magnitude as <see cref="MaxPixelCount"/>
    /// — compressed RLE data for a canvas already bounded by that pixel-count limit has no legitimate reason
    /// to exceed it. Without this, a tiny file could declare an arbitrary (up to ~4GB) biSizeImage and force
    /// that allocation immediately, regardless of how much data the stream actually contains.
    /// </summary>
    public const long MaxDeclaredImageSizeBytes = 268_435_456;
}
