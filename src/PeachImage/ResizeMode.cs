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
}
