namespace PeachImage;

/// <summary>
/// How <see cref="ResizeOptions"/> interprets the width/height passed to
/// <see cref="Image.Resize(int, int, ResizeOptions?)"/>/<see cref="AnimatedImage.Resize(int, int, ResizeOptions?)"/>.
/// </summary>
public enum ResizeMode
{
    /// <summary>Resize to exactly the given width and height, ignoring aspect ratio. The default.</summary>
    Exact,

    /// <summary>
    /// Treat the given width/height as a bounding box: scale down to the largest size that fits within it
    /// while preserving aspect ratio. Shrink-only — if the source already fits, it's returned unchanged
    /// rather than upscaled.
    /// </summary>
    Max,

    /// <summary>
    /// Scale to fill (cover) the given width/height while preserving aspect ratio, then crop the overflow
    /// so the result is exactly the requested size. Unlike <see cref="Max"/>, this can upscale. Which part
    /// of the source survives cropping is controlled by <see cref="ResizeOptions.Anchor"/>.
    /// </summary>
    Crop,

    /// <summary>
    /// Scale to fit within the given width/height while preserving aspect ratio (like <see cref="Max"/>, but
    /// without the shrink-only restriction — this can upscale), then pad the remainder so the result is
    /// exactly the requested size. Where the source sits within the padded canvas is controlled by
    /// <see cref="ResizeOptions.Anchor"/>; the fill color by <see cref="ResizeOptions.BackgroundColor"/>.
    /// </summary>
    Pad,
}
