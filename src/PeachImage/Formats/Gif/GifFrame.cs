namespace PeachImage.Formats.Gif;

/// <summary>
/// A single decoded (or to-be-encoded) frame of an animated GIF bitstream: a fully composited canvas plus
/// timing/disposal metadata. Purely internal decode/encode plumbing for <see cref="GifDecoder"/>/
/// <see cref="GifEncoder"/> — the codec-agnostic public equivalent is <see cref="AnimatedImageFrame"/>.
/// </summary>
internal sealed class GifFrame : IDisposable
{
    /// <summary>Initializes a new instance of <see cref="GifFrame"/>.</summary>
    public GifFrame(Image image, TimeSpan duration, GifDisposalMethod disposal)
    {
        ArgumentNullException.ThrowIfNull(image);

        Image = image;
        Duration = duration;
        Disposal = disposal;
    }

    /// <summary>
    /// The fully composited canvas for this frame (always <see cref="PixelFormat.Rgba32"/> — animation
    /// compositing and <see cref="GifDisposalMethod.RestoreToBackground"/> both rely on alpha).
    /// </summary>
    public Image Image { get; }

    /// <summary>How long this frame should be displayed before advancing to the next one.</summary>
    public TimeSpan Duration { get; }

    /// <summary>How this frame's canvas region should be treated once its <see cref="Duration"/> elapses.</summary>
    public GifDisposalMethod Disposal { get; }

    /// <inheritdoc/>
    public void Dispose() => Image.Dispose();
}
