namespace PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;

/// <summary>Converts studio/limited-range YUV samples to interleaved RGB.</summary>
internal interface IVp8ColorConverter
{
    /// <summary>
    /// Converts <paramref name="width"/> samples into <paramref name="rgb"/> as interleaved r,g,b triples.
    /// </summary>
    /// <remarks>
    /// A whole row per call, not a pixel per call: the per-pixel shape this replaced cost an interface dispatch
    /// and a <see cref="Span{T}"/> construction for every one of the ~2 million output pixels of a 1080p frame,
    /// and left no room for a vectorized tier to exist at all.
    /// </remarks>
    void ConvertRow(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, Span<byte> rgb, int width);
}
