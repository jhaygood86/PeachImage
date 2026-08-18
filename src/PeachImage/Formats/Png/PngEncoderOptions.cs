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

    /// <summary>
    /// Controls whether the encoder builds an indexed (PLTE, color type 3) palette instead of grayscale/
    /// truecolor. Default <see cref="PngColorMode.Auto"/>. Ignored (and indexed encoding skipped) whenever
    /// <see cref="TransparentColor"/> is set, since color-key transparency and a quantized palette target
    /// different scenarios and combining them isn't supported.
    /// </summary>
    public PngColorMode ColorMode { get; init; } = PngColorMode.Auto;

    /// <summary>
    /// The palette size, in [1, 256], indexed encoding quantizes down to. In <see cref="PngColorMode.Auto"/>
    /// mode this also doubles as the distinct-color-count threshold below which indexed output is chosen
    /// automatically — the default of 256 only auto-indexes sources that already have at most 256 distinct
    /// colors, which is always lossless. In <see cref="PngColorMode.Indexed"/> mode, a source with more
    /// distinct colors than this is quantized down via median-cut, which is lossy. Default 256.
    /// </summary>
    public int MaxColors { get; init; } = 256;

    /// <summary>
    /// Whether to apply Floyd-Steinberg error-diffusion dithering when mapping pixels down to an indexed
    /// palette. Only makes a visible difference once quantization is actually lossy (i.e.
    /// <see cref="PngColorMode.Indexed"/> with a source that has more than <see cref="MaxColors"/> distinct
    /// colors). Default <see langword="false"/>.
    /// </summary>
    public bool Dither { get; init; }
}
