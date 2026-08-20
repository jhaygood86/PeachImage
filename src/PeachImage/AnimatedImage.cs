using PeachImage.Formats.Shared.Resampling;
using PeachImage.Internal;

namespace PeachImage;

/// <summary>
/// An in-memory, fully-decoded (or to-be-encoded) animated image: an ordered sequence of composited
/// <see cref="AnimatedImageFrame"/>s plus loop count. Codec-agnostic, mirroring <see cref="Image"/>:
/// <see cref="Load(Stream, DecoderOptions?)"/> and <see cref="Save(Stream, string, EncoderOptions?)"/>
/// dispatch to whichever of <see cref="Image"/>'s built-in codecs also support animation (GIF today),
/// rather than any single format's animation API being called directly.
/// </summary>
/// <remarks>
/// <para>
/// Does not implement <see cref="IDisposable"/>: a decode-produced <see cref="Frames"/> sequence is lazy and
/// may be backed by an open stream, so nothing meaningful is owned at the <see cref="AnimatedImage"/> level
/// to dispose.
/// </para>
/// <para>
/// A decode-produced <see cref="AnimatedImageFrame.Image"/> pulled from <see cref="Frames"/> is a live view
/// into decoder-internal state, not an independent copy: the frame compositor reuses a single persistent
/// canvas across the whole animation for performance, so each frame's <c>Image</c> is only valid until the
/// <em>next</em> frame is pulled from the same enumeration (the next <c>MoveNext()</c>/loop iteration).
/// Reading pixel data (<see cref="Image.GetPixelSpan"/>, <see cref="Image.GetRowSpan"/>,
/// <see cref="Image.PixelMemory"/>) on an invalidated frame throws <see cref="InvalidOperationException"/>.
/// If you need to retain a frame beyond that point — e.g. to collect every frame before processing them, or
/// to keep the previous frame around while inspecting the current one — call
/// <see cref="AnimatedImageFrame.Clone"/> (or <see cref="Image.Clone"/> on just the pixel data) before
/// advancing:
/// <c>foreach (var frame in animated.Frames) { var kept = frame.Clone(); ... }</c>. No disposal is needed or
/// possible — frames are plain, GC-managed data once cloned.
/// </para>
/// </remarks>
public sealed class AnimatedImage
{
    private static readonly IAnimatedImageCodec[] Codecs = [.. Image.Codecs.OfType<IAnimatedImageCodec>()];
    private static readonly int MaxHeaderSize = Codecs.Length == 0 ? 0 : Codecs.Max(codec => codec.HeaderSize);

    /// <summary>Initializes a new instance of <see cref="AnimatedImage"/>.</summary>
    public AnimatedImage(IEnumerable<AnimatedImageFrame> frames, int width, int height, int loopCount)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(loopCount);

        Frames = frames;
        Width = width;
        Height = height;
        LoopCount = loopCount;
    }

    /// <summary>
    /// The frames of this animation, in display order. May be a lazily-decoded, single-pass sequence backed
    /// by an open stream — enumerate once; pass a <see cref="List{T}"/>/array to the constructor instead if
    /// you need to enumerate more than once.
    /// </summary>
    public IEnumerable<AnimatedImageFrame> Frames { get; }

    /// <summary>How many times the animation should repeat; <c>0</c> means loop forever.</summary>
    public int LoopCount { get; }

    /// <summary>The canvas width, in pixels (shared by every frame).</summary>
    public int Width { get; }

    /// <summary>The canvas height, in pixels (shared by every frame).</summary>
    public int Height { get; }

    /// <summary>Loads an animated image from <paramref name="path"/>, auto-detecting its format.</summary>
    public static AnimatedImage Load(string path, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        var fileStream = File.OpenRead(path);
        try
        {
            var animated = Load(fileStream, options);
            return new AnimatedImage(DisposeStreamWhenExhausted(animated.Frames, fileStream), animated.Width, animated.Height, animated.LoopCount);
        }
        catch
        {
            fileStream.Dispose();
            throw;
        }
    }

    /// <summary>Loads an animated image from <paramref name="stream"/>, auto-detecting its format by sniffing its header bytes.</summary>
    public static AnimatedImage Load(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var codec = ResolveCodec(stream, out var preparedStream);
        if (codec is null)
        {
            throw new UnknownImageFormatException("The image format could not be determined from the stream contents, or does not support animation.");
        }

        return codec.DecodeAnimation(preparedStream, options);
    }

    /// <summary>
    /// Creates a resized copy of this animation: every frame resized to the given dimensions using the same
    /// resampling filter, same loop count and per-frame timing/disposal. Lazy, like <see cref="Frames"/>
    /// itself — each frame is resized on demand as it's pulled, not all at once.
    /// </summary>
    public AnimatedImage Resize(int width, int height, ResizeOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        options ??= new ResizeOptions();
        return new AnimatedImage(ResizeFrames(Frames, Width, Height, width, height, options), width, height, LoopCount);
    }

    /// <summary>
    /// Creates a copy of this animation scaled down to fit within a <paramref name="maxWidth"/> x
    /// <paramref name="maxHeight"/> box, preserving aspect ratio — every frame resized the same way <see
    /// cref="Resize"/> does. Shrink-only: if this animation's canvas already fits, returns this same instance
    /// unchanged rather than resizing every frame for no reason.
    /// </summary>
    public AnimatedImage ResizeToFit(int maxWidth, int maxHeight, ResizeOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxHeight, 0);

        var (width, height) = ResizeToFitCalculator.ComputeFitDimensions(Width, Height, maxWidth, maxHeight);
        return width == Width && height == Height ? this : Resize(width, height, options);
    }

    /// <summary>Shorthand for <see cref="ResizeToFit(int, int, ResizeOptions?)"/> with a single bounding dimension applied to both axes.</summary>
    public AnimatedImage ResizeToFit(int maxDimension, ResizeOptions? options = null) =>
        ResizeToFit(maxDimension, maxDimension, options);

    /// <summary>Encodes this animated image and writes it to <paramref name="path"/>, inferring the format from the file extension.</summary>
    public void Save(string path, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        string extension = Path.GetExtension(path).TrimStart('.');
        var codec = FindCodecByExtension(extension)
            ?? throw new UnknownImageFormatException($"No built-in codec can encode animated files with extension '.{extension}'.");

        using var fileStream = File.Create(path);
        codec.EncodeAnimation(this, fileStream, options);
    }

    /// <summary>Encodes this animated image as <paramref name="formatName"/> and writes it to <paramref name="stream"/>.</summary>
    public void Save(Stream stream, string formatName, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(formatName);

        var codec = FindCodecByFormatName(formatName)
            ?? throw new UnknownImageFormatException($"No built-in codec can encode animated format '{formatName}'.", formatName);

        codec.EncodeAnimation(this, stream, options);
    }

    /// <summary>
    /// Resizes every frame to <paramref name="width"/> x <paramref name="height"/>. For every filter except
    /// <see cref="ResamplingFilter.NearestNeighbor"/> (which has no weight map to share — see
    /// <see cref="ImageResizer.Resize"/>), the horizontal/vertical <see cref="ResamplingWeightMap"/>s are
    /// built once from <paramref name="sourceWidth"/>/<paramref name="sourceHeight"/> — every frame of an
    /// <see cref="AnimatedImage"/> shares that same canvas size by construction — and reused across every
    /// frame via <see cref="ImageResizer.ResizeWithWeights"/>, rather than rebuilding them from scratch once
    /// per frame the way calling <see cref="Image.Resize"/> per frame would.
    /// </summary>
    private static IEnumerable<AnimatedImageFrame> ResizeFrames(
        IEnumerable<AnimatedImageFrame> frames, int sourceWidth, int sourceHeight, int width, int height, ResizeOptions options)
    {
        if (options.Filter == ResamplingFilter.NearestNeighbor)
        {
            foreach (var frame in frames)
            {
                yield return new AnimatedImageFrame(frame.Image.Resize(width, height, options), frame.Duration, frame.Disposal);
            }

            yield break;
        }

        var kernel = ResamplingKernelFactory.Create(options.Filter);
        var horizontalWeights = new ResamplingWeightMap(sourceWidth, width, kernel);
        var verticalWeights = new ResamplingWeightMap(sourceHeight, height, kernel);

        foreach (var frame in frames)
        {
            var resizedImage = ImageResizer.ResizeWithWeights(frame.Image, width, height, horizontalWeights, verticalWeights);
            yield return new AnimatedImageFrame(resizedImage, frame.Duration, frame.Disposal);
        }
    }

    private static IEnumerable<AnimatedImageFrame> DisposeStreamWhenExhausted(IEnumerable<AnimatedImageFrame> frames, Stream stream)
    {
        try
        {
            foreach (var frame in frames)
            {
                yield return frame;
            }
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static IAnimatedImageCodec? ResolveCodec(Stream stream, out Stream preparedStream)
    {
        var header = new byte[MaxHeaderSize];
        int read = Image.ReadFully(stream, header);
        var headerSpan = header.AsSpan(0, read);

        IAnimatedImageCodec? found = null;
        foreach (var codec in Codecs)
        {
            if (codec.CanDecode && codec.IsSupportedFileFormat(headerSpan))
            {
                found = codec;
                break;
            }
        }

        if (stream.CanSeek)
        {
            stream.Seek(-read, SeekOrigin.Current);
            preparedStream = stream;
        }
        else
        {
            preparedStream = new PrefixedStream(header.AsSpan(0, read).ToArray(), stream);
        }

        return found;
    }

    private static IAnimatedImageCodec? FindCodecByFormatName(string formatName)
    {
        foreach (var codec in Codecs)
        {
            if (codec.CanEncodeAnimation && string.Equals(codec.FormatName, formatName, StringComparison.OrdinalIgnoreCase))
            {
                return codec;
            }
        }

        return null;
    }

    private static IAnimatedImageCodec? FindCodecByExtension(string extension)
    {
        foreach (var codec in Codecs)
        {
            if (!codec.CanEncodeAnimation)
            {
                continue;
            }

            foreach (var candidate in codec.FileExtensions)
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return codec;
                }
            }
        }

        return null;
    }
}
