namespace PeachImage;

/// <summary>A single decoded (or to-be-encoded) frame of an <see cref="AnimatedImage"/>: a fully composited canvas plus timing/disposal metadata.</summary>
public sealed class AnimatedImageFrame
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
    /// When pulled from <see cref="AnimatedImage.Frames"/>, this may alias decoder-internal state and be
    /// invalidated once the next frame is requested — see the <c>Frames</c> remarks for the full contract.
    /// </summary>
    public Image Image { get; }

    /// <summary>How long this frame should be displayed before advancing to the next one.</summary>
    public TimeSpan Duration { get; }

    /// <summary>How this frame's canvas region should be treated once its <see cref="Duration"/> elapses.</summary>
    public FrameDisposalMethod Disposal { get; }

    /// <summary>
    /// Creates an independent copy of this frame, whose <see cref="Image"/> is unaffected by later frames
    /// pulled from the <see cref="AnimatedImage.Frames"/> enumeration this frame came from. Use this to
    /// retain a frame beyond the point where it would otherwise be invalidated.
    /// </summary>
    public AnimatedImageFrame Clone() => new(Image.Clone(), Duration, Disposal);
}
