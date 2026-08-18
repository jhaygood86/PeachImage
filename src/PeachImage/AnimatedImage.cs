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
/// Does not implement <see cref="IDisposable"/>: a decode-produced <see cref="Frames"/> sequence is lazy and
/// may be backed by an open stream, so nothing meaningful is owned at the <see cref="AnimatedImage"/> level
/// to dispose. Each <see cref="AnimatedImageFrame"/> pulled from <see cref="Frames"/> owns its own
/// <see cref="AnimatedImageFrame.Image"/> and must be disposed by the caller once done with it, e.g.
/// <c>foreach (var frame in animated.Frames) { using (frame) { ... } }</c>.
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
