using PeachImage.Formats.Bmp;
using PeachImage.Formats.Gif;
using PeachImage.Formats.Jpeg;

namespace PeachImage;

/// <summary>
/// Tracks the set of image codecs available to <see cref="Image.Load(Stream, DecoderOptions?)"/> and
/// <see cref="Image.Save(Stream, string, EncoderOptions?)"/>. Built-in codecs (JPEG today; future formats
/// as they're added) are registered automatically — most callers never need to touch this type. It's
/// exposed publicly only as an escape hatch for plugging in a custom/third-party codec or opting a
/// built-in one out.
/// </summary>
public static class ImageFormatManager
{
    private static readonly Lock Gate = new();
    private static readonly List<IImageCodec> Codecs = [];
    private static int _maxDecoderHeaderSize;

    static ImageFormatManager()
    {
        Register(JpegCodec.Instance);
        Register(BmpCodec.Instance);
        Register(GifCodec.Instance);
    }

    /// <summary>The codecs currently registered, in registration order.</summary>
    public static IReadOnlyCollection<IImageCodec> RegisteredCodecs
    {
        get
        {
            lock (Gate)
            {
                return [.. Codecs];
            }
        }
    }

    /// <summary>Registers <paramref name="codec"/>, making its decoder/encoder available to <see cref="Image"/>.</summary>
    public static void Register(IImageCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);

        lock (Gate)
        {
            Codecs.RemoveAll(c => string.Equals(c.FormatName, codec.FormatName, StringComparison.OrdinalIgnoreCase));
            Codecs.Add(codec);
            _maxDecoderHeaderSize = Codecs
                .Select(c => c.Decoder?.HeaderSize ?? 0)
                .DefaultIfEmpty(0)
                .Max();
        }
    }

    /// <summary>Removes the codec registered under <paramref name="formatName"/>, if any. Returns whether one was removed.</summary>
    public static bool Unregister(string formatName)
    {
        ArgumentNullException.ThrowIfNull(formatName);

        lock (Gate)
        {
            int removed = Codecs.RemoveAll(c => string.Equals(c.FormatName, formatName, StringComparison.OrdinalIgnoreCase));
            _maxDecoderHeaderSize = Codecs
                .Select(c => c.Decoder?.HeaderSize ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            return removed > 0;
        }
    }

    /// <summary>The largest <see cref="IImageDecoder.HeaderSize"/> across all registered decoders.</summary>
    internal static int MaxDecoderHeaderSize
    {
        get
        {
            lock (Gate)
            {
                return _maxDecoderHeaderSize;
            }
        }
    }

    /// <summary>Finds the first registered decoder whose <see cref="IImageDecoder.IsSupportedFileFormat"/> matches <paramref name="header"/>.</summary>
    internal static IImageDecoder? FindDecoder(ReadOnlySpan<byte> header)
    {
        lock (Gate)
        {
            foreach (var codec in Codecs)
            {
                if (codec.Decoder is { } decoder && decoder.IsSupportedFileFormat(header))
                {
                    return decoder;
                }
            }
        }

        return null;
    }

    /// <summary>Finds the registered encoder for the given format name, case-insensitively.</summary>
    internal static IImageEncoder? FindEncoderByFormatName(string formatName)
    {
        lock (Gate)
        {
            foreach (var codec in Codecs)
            {
                if (codec.Encoder is { } encoder && string.Equals(encoder.FormatName, formatName, StringComparison.OrdinalIgnoreCase))
                {
                    return encoder;
                }
            }
        }

        return null;
    }

    /// <summary>Finds the registered encoder whose <see cref="IImageEncoder.FileExtensions"/> contains <paramref name="extension"/> (without a leading dot), case-insensitively.</summary>
    internal static IImageEncoder? FindEncoderByExtension(string extension)
    {
        lock (Gate)
        {
            foreach (var codec in Codecs)
            {
                if (codec.Encoder is not { } encoder)
                {
                    continue;
                }

                foreach (var candidate in encoder.FileExtensions)
                {
                    if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                    {
                        return encoder;
                    }
                }
            }
        }

        return null;
    }
}
