using PeachImage.Formats.Webp.Decoding;
using PeachImage.Formats.Webp.Decoding.Alpha;
using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Formats.Webp;

/// <summary>
/// Decodes WebP images: both the VP8 (lossy) and VP8L (lossless) bitstreams, including alpha (<c>ALPH</c>
/// chunk), in the RIFF "simple" and "extended" (non-animated) container formats. Animated WebP files
/// (<c>VP8X</c>'s animation flag, or an <c>ANIM</c>/<c>ANMF</c> chunk) are not yet supported and cause
/// <see cref="Decode"/> to throw rather than silently decoding a single sub-frame as if it were the whole
/// canvas — a WebP animation frame is frequently a partial-canvas sub-rectangle meant to be composited with
/// blend/dispose semantics, so treating it alone as the full image would produce a silently wrong result.
/// </summary>
public sealed class WebpDecoder : IImageDecoder
{
    /// <inheritdoc/>
    public string FormatName => "webp";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["webp"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/webp"];

    /// <inheritdoc/>
    public int HeaderSize => 12;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) =>
        header.Length >= 12
        && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
        && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P';

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var metadata = new ImageMetadata();
        var container = WebpContainerReader.Read(stream, metadata);

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

        var pixelFormat = hasAlpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
        return new ImageInfo(width, height, pixelFormat, FormatName);
    }

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var metadata = new ImageMetadata();
        var container = WebpContainerReader.Read(stream, metadata);

        var image = container.Format == WebpBitstreamFormat.Lossless
            ? Vp8LDecoder.Decode(container.BitstreamData)
            : DecodeLossy(container);

        if (container.CanvasWidth is { } canvasWidth && container.CanvasHeight is { } canvasHeight
            && (image.Width != canvasWidth || image.Height != canvasHeight))
        {
            image.Dispose();
            throw new WebpDecodingException(
                $"WebP VP8X canvas size {canvasWidth}x{canvasHeight} does not match the decoded bitstream size {image.Width}x{image.Height}.");
        }

        foreach (var profile in metadata.Profiles)
        {
            image.Metadata.Profiles.Add(profile);
        }

        return Decoding.PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
    }

    /// <summary>
    /// Decodes the alpha plane (if any) at the bitstream's own dimensions first, then hands it into the VP8
    /// frame decoder so pixel conversion can emit RGBA32 directly instead of building an RGB24 image and
    /// widening it afterward -- see <see cref="Vp8FrameDecoder.ProduceRgbFrame"/>'s remarks for why that
    /// matters. Dimensions are read via a cheap peek rather than waiting on the frame decoder's own parse,
    /// since alpha decode needs them first; both read the identical bytes, so they can't disagree.
    /// </summary>
    private static Image DecodeLossy(WebpContainerInfo container)
    {
        byte[]? alphaPlane = null;

        if (container.AlphaData is { } alphaData)
        {
            if (!WebpBitstreamHeaderPeek.TryPeekVp8(container.BitstreamData, out int width, out int height))
            {
                throw new WebpDecodingException("Malformed VP8 chunk: missing or invalid keyframe start code.");
            }

            alphaPlane = WebpAlphaDecoder.Decode(alphaData, width, height);
        }

        var frame = Vp8FrameDecoder.Instance.Decode(container.BitstreamData, alphaPlane);
        return Image.FromBuffer(frame.Width, frame.Height, frame.PixelFormat, frame.Pixels);
    }
}
