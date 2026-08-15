namespace PeachImage.Formats.Gif;

/// <summary>An in-memory, fully-decoded (or to-be-encoded) animated GIF: an ordered sequence of composited <see cref="GifFrame"/>s plus loop count.</summary>
public sealed class GifImage : IDisposable
{
    /// <summary>Initializes a new instance of <see cref="GifImage"/>.</summary>
    public GifImage(IReadOnlyList<GifFrame> frames, int loopCount)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(loopCount);

        Frames = frames;
        LoopCount = loopCount;
    }

    /// <summary>The frames of this animation, in display order. Always has at least one frame.</summary>
    public IReadOnlyList<GifFrame> Frames { get; }

    /// <summary>How many times the animation should repeat; <c>0</c> means loop forever (NETSCAPE2.0 semantics).</summary>
    public int LoopCount { get; }

    /// <summary>The canvas width, in pixels (shared by every frame).</summary>
    public int Width => Frames[0].Image.Width;

    /// <summary>The canvas height, in pixels (shared by every frame).</summary>
    public int Height => Frames[0].Image.Height;

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var frame in Frames)
        {
            frame.Dispose();
        }
    }
}
