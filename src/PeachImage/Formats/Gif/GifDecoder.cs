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

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = GifHeaderReader.Read(stream);
        bool hasTransparency = false;
        bool isAnimated = false;
        try
        {
            (hasTransparency, isAnimated) = GifAnimationScanner.ScanForTransparencyAndAnimation(stream);
        }
        catch (GifDecodingException)
        {
            // Malformed frame data beyond the header: Identify only promises header-level info, so degrade
            // gracefully rather than failing what would otherwise be a successful dimension/format lookup.
        }

        var pixelFormat = hasTransparency ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
        return new ImageInfo(header.Width, header.Height, pixelFormat, FormatName, isAnimated, HasAlpha: pixelFormat.HasAlpha());
    }

    /// <summary>Decodes just the first frame (equivalent to how most viewers render a GIF as a static image).</summary>
    public static Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var image = GifSingleFrameDecoder.Decode(stream);
        bool hasAlpha = image.PixelFormat.HasAlpha();

        bool isAnimated;
        try
        {
            isAnimated = GifAnimationScanner.HasAnotherFrame(stream);
        }
        catch (GifDecodingException)
        {
            // Frame 1 decoded successfully; trailing data beyond it is malformed/truncated. Don't fail an
            // otherwise-successful single-frame decode just because we can't tell whether more frames follow.
            isAnimated = false;
        }

        var result = PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
        if (!ReferenceEquals(result, image))
        {
            image.Dispose();
        }

        result.IsAnimated = isAnimated;
        result.HasAlpha = hasAlpha;
        return result;
    }

    /// <summary>
    /// Decodes every frame of the animation, fully composited, with per-frame timing/disposal metadata and
    /// the NETSCAPE2.0 loop count. <see cref="AnimatedImage.Frames"/> is lazy: frame 1 is decoded eagerly here
    /// (so a malformed frame 1 throws immediately, and so <see cref="AnimatedImage.Frames"/> always has at
    /// least one frame), but every subsequent frame is decoded only as the caller enumerates.
    /// </summary>
    /// <remarks>
    /// The NETSCAPE2.0 loop-count extension conventionally precedes every frame (this library's own encoder
    /// always writes it first), but the format doesn't strictly guarantee it. <see cref="AnimatedImage.LoopCount"/>
    /// reflects whatever's been seen by the time frame 1 is decoded; a non-conformant file with the loop
    /// extension after frame 1 yields a loop count of 0.
    /// </remarks>
    public static AnimatedImage DecodeAnimation(Stream stream, GifDecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _ = options;

        var header = GifImageDecoder.ReadHeader(stream);
        var prelude = GifImageDecoder.ReadPrelude(stream);
        var enumerator = DecodeAnimatedFrames(stream, header, prelude).GetEnumerator();
        try
        {
            enumerator.MoveNext();
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }

        return new AnimatedImage(PrependCurrent(enumerator), header.Width, header.Height, prelude.LoopCount);
    }

    private static IEnumerable<AnimatedImageFrame> DecodeAnimatedFrames(Stream stream, GifHeader header, GifImageDecoder.GifPrelude prelude)
    {
        foreach (var frame in GifImageDecoder.DecodeFrames(stream, header, prelude, GifDecodingLimits.MaxFrameCount, GifDecodingLimits.MaxCumulativeCanvasBytes))
        {
            yield return new AnimatedImageFrame(frame.Image, frame.Duration, ToFrameDisposalMethod(frame.Disposal));
        }
    }

    private static IEnumerable<AnimatedImageFrame> PrependCurrent(IEnumerator<AnimatedImageFrame> enumerator)
    {
        try
        {
            yield return enumerator.Current;
            while (enumerator.MoveNext())
            {
                yield return enumerator.Current;
            }
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    private static FrameDisposalMethod ToFrameDisposalMethod(GifDisposalMethod disposal) => disposal switch
    {
        GifDisposalMethod.DoNotDispose => FrameDisposalMethod.DoNotDispose,
        GifDisposalMethod.RestoreToBackground => FrameDisposalMethod.RestoreToBackground,
        GifDisposalMethod.RestoreToPrevious => FrameDisposalMethod.RestoreToPrevious,
        _ => FrameDisposalMethod.None,
    };
}
