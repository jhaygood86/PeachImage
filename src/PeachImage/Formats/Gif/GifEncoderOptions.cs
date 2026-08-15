namespace PeachImage.Formats.Gif;

/// <summary>GIF-specific encode options.</summary>
public sealed class GifEncoderOptions : EncoderOptions
{
    /// <summary>The maximum palette size to quantize down to. Clamped to [2, 256]. Default 256.</summary>
    public int MaxColors { get; init; } = 256;

    /// <summary>Whether to apply Floyd-Steinberg error-diffusion dithering when mapping pixels to the quantized palette. Default <see langword="false"/>.</summary>
    public bool Dither { get; init; }
}
