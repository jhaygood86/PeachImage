using PeachImage.Formats.Gif.Decoding;
using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Formats.Gif;

/// <summary>
/// Decodes Graphic Interchange Format (GIF) images, including GIF87a/GIF89a, interlacing, transparency,
/// and multi-frame animation (disposal methods, per-frame delay, NETSCAPE2.0 loop count) via
/// <see cref="DecodeAnimation"/>. Used internally by <see cref="GifCodec"/>; animation is exposed publicly
/// through the codec-agnostic <see cref="AnimatedImage"/>, not through this type.
/// </summary>
internal static class GifDecoder
{
    private const string FormatName = "gif";
    private const byte ExtensionIntroducer = 0x21;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = GifHeaderReader.Read(stream);
        bool hasTransparency = ScanForTransparency(stream);
        var pixelFormat = hasTransparency ? PixelFormat.Rgba32 : PixelFormat.Rgb24;

        return new ImageInfo(header.Width, header.Height, pixelFormat, FormatName);
    }

    /// <summary>Decodes just the first frame (equivalent to how most viewers render a GIF as a static image).</summary>
    public static Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var image = GifSingleFrameDecoder.Decode(stream);
        return PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
    }

    /// <summary>Decodes every frame of the animation, fully composited, with per-frame timing/disposal metadata and the NETSCAPE2.0 loop count.</summary>
    public static AnimatedImage DecodeAnimation(Stream stream, GifDecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _ = options;

        var (frames, loopCount) = GifImageDecoder.Decode(stream, maxFrames: GifDecodingLimits.MaxFrameCount);
        var animatedFrames = new List<AnimatedImageFrame>(frames.Count);
        foreach (var frame in frames)
        {
            animatedFrames.Add(new AnimatedImageFrame(frame.Image, frame.Duration, ToFrameDisposalMethod(frame.Disposal)));
        }

        return new AnimatedImage(animatedFrames, loopCount);
    }

    private static FrameDisposalMethod ToFrameDisposalMethod(GifDisposalMethod disposal) => disposal switch
    {
        GifDisposalMethod.DoNotDispose => FrameDisposalMethod.DoNotDispose,
        GifDisposalMethod.RestoreToBackground => FrameDisposalMethod.RestoreToBackground,
        GifDisposalMethod.RestoreToPrevious => FrameDisposalMethod.RestoreToPrevious,
        _ => FrameDisposalMethod.None,
    };

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
