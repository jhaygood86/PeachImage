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

    /// <summary>
    /// Which part of the source survives cropping (<see cref="ResizeMode.Crop"/>), or where the source sits
    /// within the padded canvas (<see cref="ResizeMode.Pad"/>). Defaults to <see cref="AnchorPosition.MiddleCenter"/>.
    /// Ignored for <see cref="ResizeMode.Exact"/> and <see cref="ResizeMode.Max"/>.
    /// </summary>
    public AnchorPosition Anchor { get; init; } = AnchorPosition.MiddleCenter;

    /// <summary>
    /// The fill color used by <see cref="ResizeMode.Pad"/> for the padded border around the source. When
    /// <see langword="null"/>, defaults to white if the target <see cref="PixelFormat"/> has no alpha
    /// channel, or transparent if it does. Ignored for every other <see cref="ResizeMode"/>.
    /// </summary>
    public (byte R, byte G, byte B, byte A)? BackgroundColor { get; init; }
}
