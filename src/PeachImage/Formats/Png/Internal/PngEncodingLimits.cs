namespace PeachImage.Formats.Png.Internal;

/// <summary>Tunables applied while encoding.</summary>
internal static class PngEncodingLimits
{
    /// <summary>The maximum size, in bytes, of a single emitted IDAT chunk's data. Larger compressed output is split across multiple consecutive IDAT chunks.</summary>
    public const int IdatChunkSize = 64 * 1024;
}
