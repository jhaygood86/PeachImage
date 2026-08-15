namespace PeachImage.Formats.Bmp.Internal;

/// <summary>Sanity-check limits applied while decoding, to reject hostile/corrupt input before large allocations. Mirrors Jpeg's JpegDecodingLimits.</summary>
internal static class BmpDecodingLimits
{
    /// <summary>The largest pixel count (width * height) a decode will attempt to allocate.</summary>
    public const long MaxPixelCount = 268_435_456;
}
