using PeachImage.Internal;

namespace PeachImage;

/// <summary>
/// An in-memory, fully-decoded (or to-be-encoded) animated image: an ordered sequence of composited
/// <see cref="AnimatedImageFrame"/>s plus loop count. Codec-agnostic, mirroring <see cref="Image"/>:
/// <see cref="Load(Stream, DecoderOptions?)"/> and <see cref="Save(Stream, string, EncoderOptions?)"/>
/// dispatch to whichever of <see cref="Image"/>'s built-in codecs also support animation (GIF today),
/// rather than any single format's animation API being called directly.
/// </summary>
public sealed class AnimatedImage : IDisposable
{
    private static readonly IAnimatedImageCodec[] Codecs = [.. Image.Codecs.OfType<IAnimatedImageCodec>()];
    private static readonly int MaxHeaderSize = Codecs.Length == 0 ? 0 : Codecs.Max(codec => codec.HeaderSize);

    /// <summary>Initializes a new instance of <see cref="AnimatedImage"/>.</summary>
    public AnimatedImage(IReadOnlyList<AnimatedImageFrame> frames, int loopCount)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(loopCount);

        Frames = frames;
        LoopCount = loopCount;
    }

    /// <summary>The frames of this animation, in display order. Always has at least one frame.</summary>
    public IReadOnlyList<AnimatedImageFrame> Frames { get; }

    /// <summary>How many times the animation should repeat; <c>0</c> means loop forever.</summary>
    public int LoopCount { get; }

    /// <summary>The canvas width, in pixels (shared by every frame).</summary>
    public int Width => Frames[0].Image.Width;

    /// <summary>The canvas height, in pixels (shared by every frame).</summary>
    public int Height => Frames[0].Image.Height;

    /// <summary>Loads an animated image from <paramref name="path"/>, auto-detecting its format.</summary>
    public static AnimatedImage Load(string path, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fileStream = File.OpenRead(path);
        return Load(fileStream, options);
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

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var frame in Frames)
        {
            frame.Dispose();
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
            if (codec.CanEncode && string.Equals(codec.FormatName, formatName, StringComparison.OrdinalIgnoreCase))
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
            if (!codec.CanEncode)
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
