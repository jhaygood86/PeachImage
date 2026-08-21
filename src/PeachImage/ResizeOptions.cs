namespace PeachImage;

/// <summary>Options controlling <see cref="Image.Resize(int, int, ResizeOptions?)"/> and <see cref="AnimatedImage.Resize(int, int, ResizeOptions?)"/>.</summary>
public class ResizeOptions
{
    /// <summary>The resampling filter to reconstruct pixel values with. Defaults to <see cref="ResamplingFilter.Bicubic"/>.</summary>
    public ResamplingFilter Filter { get; init; } = ResamplingFilter.Bicubic;

    /// <summary>
    /// How the requested width/height is interpreted. Defaults to <see cref="ResizeMode.Exact"/>. Set to
    /// <see cref="ResizeMode.Max"/> to instead treat width/height as a bounding box — the largest size that
    /// fits within it, preserving aspect ratio, without ever upscaling.
    /// </summary>
    public ResizeMode Mode { get; init; } = ResizeMode.Exact;
}
