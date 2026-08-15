namespace PeachImage.Formats.Png;

/// <summary>PNG-specific encode options.</summary>
public sealed class PngEncoderOptions : EncoderOptions
{
    /// <summary>How the encoder picks a per-scanline filter type. Defaults to <see cref="PngFilterStrategy.Adaptive"/>.</summary>
    public PngFilterStrategy FilterStrategy { get; init; } = PngFilterStrategy.Adaptive;

    /// <summary>Whether to Adam7-interlace the output. Defaults to <see langword="false"/> (sequential scan).</summary>
    public bool Interlace { get; init; }

    /// <summary>The zlib compression level/speed tradeoff. Defaults to <see cref="PngCompressionLevel.Default"/>.</summary>
    public PngCompressionLevel CompressionLevel { get; init; } = PngCompressionLevel.Default;

    /// <summary>
    /// When set, and the source image has no alpha channel, encodes a <c>tRNS</c> chunk marking this
    /// single RGB (or gray, for single-channel sources) value as fully transparent, instead of the
    /// default of never emitting a color-key transparency chunk. Ignored for sources that already have
    /// a real alpha channel (<see cref="PixelFormat.Rgba32"/>/<see cref="PixelFormat.Rgba64"/>), which
    /// always encode with a true per-pixel alpha channel.
    /// </summary>
    public (byte R, byte G, byte B)? TransparentColor { get; init; }
}
