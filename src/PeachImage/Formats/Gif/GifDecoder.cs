using PeachImage.Formats.Gif.Decoding;
using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Formats.Gif;

/// <summary>
/// Decodes Graphic Interchange Format (GIF) images, including GIF87a/GIF89a, interlacing, transparency,
/// and multi-frame animation (disposal methods, per-frame delay, NETSCAPE2.0 loop count) via
/// <see cref="DecodeAnimation"/>.
/// </summary>
public sealed class GifDecoder : IImageDecoder
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;

    /// <inheritdoc/>
    public string FormatName => "gif";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["gif"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/gif"];

    /// <inheritdoc/>
    public int HeaderSize => 6;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) =>
        header.Length >= 6
        && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'8'
        && (header[4] == (byte)'7' || header[4] == (byte)'9') && header[5] == (byte)'a';

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = GifHeaderReader.Read(stream);
        bool hasTransparency = ScanForTransparency(stream);
        var pixelFormat = hasTransparency ? PixelFormat.Rgba32 : PixelFormat.Rgb24;

        return new ImageInfo(header.Width, header.Height, pixelFormat, FormatName);
    }

    /// <summary>Decodes just the first frame (equivalent to how most viewers render a GIF as a static image).</summary>
    public Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var image = GifSingleFrameDecoder.Decode(stream);
        return PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
    }

    /// <summary>Decodes every frame of the animation, fully composited, with per-frame timing/disposal metadata and the NETSCAPE2.0 loop count.</summary>
    public static GifImage DecodeAnimation(Stream stream, GifDecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _ = options;

        var (frames, loopCount) = GifImageDecoder.Decode(stream, maxFrames: GifDecodingLimits.MaxFrameCount);
        return new GifImage(frames, loopCount);
    }

    private static bool ScanForTransparency(Stream stream)
    {
        while (true)
        {
            if (!GifStreamHelpers.TryReadByte(stream, out byte blockType) || blockType == Trailer)
            {
                return false;
            }

            if (blockType == ExtensionIntroducer)
            {
                var (gce, _) = GifExtensionReader.Read(stream);
                if (gce is not null)
                {
                    return gce.TransparentColorIndex.HasValue;
                }

                continue;
            }

            // Reached image data (or an unrecognized block) without seeing a Graphic Control Extension.
            return false;
        }
    }
}
