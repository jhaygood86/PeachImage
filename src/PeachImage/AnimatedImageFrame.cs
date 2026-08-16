namespace PeachImage;

/// <summary>A single decoded (or to-be-encoded) frame of an <see cref="AnimatedImage"/>: a fully composited canvas plus timing/disposal metadata.</summary>
public sealed class AnimatedImageFrame : IDisposable
{
    /// <summary>Initializes a new instance of <see cref="AnimatedImageFrame"/>.</summary>
    public AnimatedImageFrame(Image image, TimeSpan duration, FrameDisposalMethod disposal)
    {
        ArgumentNullException.ThrowIfNull(image);

        Image = image;
        Duration = duration;
        Disposal = disposal;
    }

    /// <summary>
    /// The fully composited canvas for this frame (always <see cref="PixelFormat.Rgba32"/> — animation
    /// compositing and <see cref="FrameDisposalMethod.RestoreToBackground"/> both rely on alpha).
    /// </summary>
    public Image Image { get; }

    /// <summary>How long this frame should be displayed before advancing to the next one.</summary>
    public TimeSpan Duration { get; }

    /// <summary>How this frame's canvas region should be treated once its <see cref="Duration"/> elapses.</summary>
    public FrameDisposalMethod Disposal { get; }

    /// <inheritdoc/>
    public void Dispose() => Image.Dispose();
}
