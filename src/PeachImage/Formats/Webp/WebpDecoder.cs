using PeachImage.Formats.Webp.Decoding;

namespace PeachImage.Formats.Webp;

/// <summary>
/// Decodes WebP images: both the VP8 (lossy) and VP8L (lossless) bitstreams, including alpha (<c>ALPH</c>
/// chunk), in the RIFF "simple" and "extended" container formats, plus animated WebP (<c>VP8X</c>'s animation
/// flag, an <c>ANIM</c> chunk, and one or more <c>ANMF</c> frame chunks) via <see cref="DecodeAnimation"/>.
/// <see cref="Decode"/> decodes just the first frame of an animated file (see <see cref="Image.IsAnimated"/>),
/// matching <c>GifDecoder.Decode</c>'s equivalent "decode just the first frame" convention. Used internally by
/// <see cref="WebpCodec"/>; animation is exposed publicly through the codec-agnostic <see cref="AnimatedImage"/>,
/// not through this type.
/// </summary>
internal static class WebpDecoder
{
    private const string FormatName = "webp";

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prelude = WebpContainerReader.ReadPrelude(stream, out var pendingHeader);

        if (prelude.HasAnimation)
        {
            // Reported directly from VP8X — no ANIM chunk or frame decode needed, matching GifDecoder.Identify's
            // "header-level info only" leniency (an animated file with a malformed/missing ANIM chunk would
            // still successfully Identify, even though DecodeAnimation would throw on it).
            var pixelFormat = prelude.HasAlpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
            return new ImageInfo(prelude.CanvasWidth!.Value, prelude.CanvasHeight!.Value, pixelFormat, FormatName, IsAnimated: true);
        }

        var metadata = new ImageMetadata();
        var container = WebpContainerReader.Read(stream, metadata, prelude, pendingHeader);

        bool hasAlpha;
        int width, height;

        if (container.Format == WebpBitstreamFormat.Lossless)
        {
            if (!WebpBitstreamHeaderPeek.TryPeekVp8L(container.BitstreamData, out width, out height, out hasAlpha))
            {
                throw new WebpDecodingException("Malformed VP8L chunk: missing or invalid signature byte.");
            }
        }
        else
        {
            if (!WebpBitstreamHeaderPeek.TryPeekVp8(container.BitstreamData, out width, out height))
            {
                throw new WebpDecodingException("Malformed VP8 chunk: missing or invalid keyframe start code.");
            }

            hasAlpha = container.AlphaData is not null;
        }

        var format = hasAlpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
        return new ImageInfo(width, height, format, FormatName);
    }

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>. Decodes just the first, fully composited frame if the file is animated.</summary>
    public static Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var metadata = new ImageMetadata();
        var prelude = WebpContainerReader.ReadPrelude(stream, out var pendingHeader);

        Image image;
        bool isAnimated;

        if (prelude.HasAnimation)
        {
            var header = WebpAnimationReader.ReadHeader(stream, metadata, prelude);
            using var frames = WebpAnimationReader.ReadFrames(stream, metadata, header).GetEnumerator();
            if (!frames.MoveNext())
            {
                throw new WebpDecodingException("Animated WebP file contains no ANMF frames.");
            }

            image = frames.Current.Image;
            isAnimated = true;
        }
        else
        {
            var container = WebpContainerReader.Read(stream, metadata, prelude, pendingHeader);
            image = WebpBitstreamDecoder.Decode(container.Format, container.BitstreamData, container.AlphaData);
            isAnimated = false;

            if (container.CanvasWidth is { } canvasWidth && container.CanvasHeight is { } canvasHeight
                && (image.Width != canvasWidth || image.Height != canvasHeight))
            {
                image.Dispose();
                throw new WebpDecodingException(
                    $"WebP VP8X canvas size {canvasWidth}x{canvasHeight} does not match the decoded bitstream size {image.Width}x{image.Height}.");
            }
        }

        foreach (var profile in metadata.Profiles)
        {
            image.Metadata.Profiles.Add(profile);
        }

        var result = Decoding.PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
        result.IsAnimated = isAnimated;
        return result;
    }

    /// <summary>
    /// Decodes every frame of the animation, fully composited, with per-frame timing/disposal metadata and
    /// the ANIM chunk's loop count. <see cref="AnimatedImage.Frames"/> is lazy: frame 1 is decoded eagerly
    /// here (so a malformed frame 1 throws immediately, and so <see cref="AnimatedImage.Frames"/> always has
    /// at least one frame), but every subsequent frame is decoded only as the caller enumerates.
    /// </summary>
    public static AnimatedImage DecodeAnimation(Stream stream, WebpDecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _ = options;

        var metadata = new ImageMetadata();
        var prelude = WebpContainerReader.ReadPrelude(stream, out _);
        if (!prelude.HasAnimation)
        {
            throw new WebpDecodingException("WebP file is not animated (no VP8X animation flag).");
        }

        var header = WebpAnimationReader.ReadHeader(stream, metadata, prelude);
        var enumerator = WebpAnimationReader.ReadFrames(stream, metadata, header).GetEnumerator();

        bool hasFrame;
        try
        {
            hasFrame = enumerator.MoveNext();
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }

        if (!hasFrame)
        {
            enumerator.Dispose();
            throw new WebpDecodingException("Animated WebP file contains no ANMF frames.");
        }

        return new AnimatedImage(PrependCurrent(enumerator), header.CanvasWidth, header.CanvasHeight, header.LoopCount);
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
}
