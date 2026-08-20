namespace PeachImage;

/// <summary>Options controlling <see cref="Image.Resize(int, int, ResizeOptions?)"/> and <see cref="AnimatedImage.Resize(int, int, ResizeOptions?)"/>.</summary>
public class ResizeOptions
{
    /// <summary>The resampling filter to reconstruct pixel values with. Defaults to <see cref="ResamplingFilter.Bicubic"/>.</summary>
    public ResamplingFilter Filter { get; init; } = ResamplingFilter.Bicubic;
}
