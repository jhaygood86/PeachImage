using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Container;
using PeachImage.Formats.Jxl.Features;
using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Jxl.Internal;

namespace PeachImage.Formats.Jxl;

/// <summary>Decodes JPEG XL images. Used internally by <see cref="JxlCodec"/>.</summary>
internal static class JxlDecoder
{
    private const string FormatName = "jxl";

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without decoding pixel data.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var container = JxlContainer.Parse(ReadAll(stream));
        var headers = JxlCodestreamHeaders.Read(container.Codestream.Span, readIcc: false);
        var metadata = headers.Metadata;

        // Orientations 5..8 transpose the image.
        bool transposed = metadata.Orientation >= 5;
        int width = checked((int)(transposed ? headers.Size.Height : headers.Size.Width));
        int height = checked((int)(transposed ? headers.Size.Width : headers.Size.Height));
        return new ImageInfo(
            width,
            height,
            JxlPixelFormatSelector.Select(metadata),
            FormatName,
            IsAnimated: metadata.Animation is not null,
            HasAlpha: metadata.AlphaChannelIndex >= 0,
            IsLosslessEncoding: IsLossless(container, metadata));
    }

    /// <summary>
    /// Whether the pixels are stored losslessly: the colour channels are in the original space (not XYB) and the first frame is Modular.
    /// A JPEG-reconstruction file is also non-XYB but its frame is VarDCT, so it is not lossless with respect to its pixels.
    /// A preview frame, when present, is skipped so the real image's first frame is the one inspected.
    /// </summary>
    private static bool IsLossless(JxlContainer container, JxlImageMetadata metadata)
    {
        if (metadata.XybEncoded)
        {
            return false;
        }

        // The frame offset is only known once the ICC profile (if any) has been read past.
        var codestream = container.Codestream.ToArray();
        var headers = JxlCodestreamHeaders.Read(codestream);
        int offset = headers.FrameOffset;
        if (headers.Metadata.PreviewSize is { } previewSize)
        {
            // Step over the preview (header, TOC and section data) to reach the real first frame.
            offset += FrameByteLength(codestream, offset, headers.Metadata, previewSize, isPreview: true);
        }

        var reader = new JxlBitReader(codestream.AsSpan(offset));
        return JxlFrameHeader.Read(ref reader, headers.Metadata, headers.Size).IsModular;
    }

    /// <summary>The total byte length of the frame at <paramref name="offset"/>: its header, table of contents and section data.</summary>
    private static int FrameByteLength(byte[] codestream, int offset, JxlImageMetadata metadata, JxlSize size, bool isPreview)
    {
        var reader = new JxlBitReader(codestream.AsSpan(offset));
        var frame = JxlFrameHeader.Read(ref reader, metadata, size, isPreview);
        var dims = frame.Dimensions;
        var toc = JxlToc.Read(ref reader, JxlToc.EntryCount(dims.NumGroups, dims.NumDcGroups, frame.NumPasses));
        long total = reader.BitPosition >> 3;
        foreach (uint section in toc.Sizes)
        {
            total += section;
        }

        if (offset + total > codestream.Length)
        {
            throw new JxlDecodingException("The frame's sections extend past the end of the codestream.");
        }

        return (int)total;
    }

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>. For an animation, decodes the first frame (as composited onto the canvas).</summary>
    public static Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var (container, headers) = Open(stream);
        foreach (var (frame, _) in VisibleFrames(container.Codestream.ToArray(), headers))
        {
            using (frame)
            {
                var built = JxlImageBuilder.Build(frame, headers, options?.TargetPixelFormat);
                return Finish(built, container, headers);
            }
        }

        throw new JxlDecodingException("The codestream contains no visible frame.");
    }

    /// <summary>
    /// Decodes <paramref name="stream"/> as an animation: every visible frame composited onto the canvas, as RGBA. Frames are
    /// decoded lazily as the sequence is enumerated; a still image yields one frame.
    /// </summary>
    public static AnimatedImage DecodeAnimation(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _ = options;

        var (container, headers) = Open(stream);
        var metadata = headers.Metadata;
        bool transposed = metadata.Orientation >= 5;
        int width = checked((int)(transposed ? headers.Size.Height : headers.Size.Width));
        int height = checked((int)(transposed ? headers.Size.Width : headers.Size.Height));
        int loops = metadata.Animation is { } animation ? checked((int)Math.Min(animation.NumLoops, int.MaxValue)) : 1;
        return new AnimatedImage(EnumerateAnimation(container, headers), width, height, loops);
    }

    /// <summary>Decodes every visible frame (composited onto the canvas) into <paramref name="format"/>. Used to check frames at full precision.</summary>
    internal static List<Image> DecodeFrames(Stream stream, PixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var (container, headers) = Open(stream);
        var images = new List<Image>();
        try
        {
            foreach (var (frame, _) in VisibleFrames(container.Codestream.ToArray(), headers))
            {
                using (frame)
                {
                    images.Add(Finish(JxlImageBuilder.Build(frame, headers, format), container, headers));
                }
            }

            return images;
        }
        catch
        {
            images.ForEach(image => image.Dispose());
            throw;
        }
    }

    private static IEnumerable<AnimatedImageFrame> EnumerateAnimation(JxlContainer container, JxlCodestreamHeaders headers)
    {
        var animation = headers.Metadata.Animation;
        foreach (var (frame, ticks) in VisibleFrames(container.Codestream.ToArray(), headers))
        {
            Image image;
            using (frame)
            {
                image = Finish(JxlImageBuilder.Build(frame, headers, PixelFormat.Rgba32), container, headers);
            }

            TimeSpan duration = TimeSpan.Zero;
            if (animation is { } a && a.TicksPerSecondNumerator != 0)
            {
                duration = TimeSpan.FromTicks((long)Math.Round(ticks * (double)a.TicksPerSecondDenominator * TimeSpan.TicksPerSecond / a.TicksPerSecondNumerator));
            }

            yield return new AnimatedImageFrame(image, duration, FrameDisposalMethod.None);
        }
    }

    private static (JxlContainer Container, JxlCodestreamHeaders Headers) Open(Stream stream)
    {
        var container = JxlContainer.Parse(ReadAll(stream));
        var headers = JxlCodestreamHeaders.Read(container.Codestream.ToArray());
        if (headers.Metadata.ColorEncoding.ColorSpace == JxlColorSpace.Unknown)
        {
            throw new JxlUnsupportedFeatureException("Images with an unknown colour space (for example CMYK) are not supported yet.");
        }

        return (container, headers);
    }

    /// <summary>
    /// Walks the codestream's frames in order, keeping the frames later ones refer to, and yields each visible frame (a last frame,
    /// or one with a non-zero duration) composited onto the canvas, with its duration in animation ticks. The caller disposes each.
    /// </summary>
    private static IEnumerable<(JxlDecodedFrame Frame, uint Ticks)> VisibleFrames(byte[] codestream, JxlCodestreamHeaders headers)
    {
        var state = new JxlDecoderState();
        int offset = headers.FrameOffset;
        if (headers.Metadata.PreviewSize is not null)
        {
            // The preview is a small stand-in for the image; it precedes the real frames and is not part of them.
            using var preview = JxlFrameDecoder.Decode(codestream, offset, headers, state, isPreview: true);
            offset += preview.ByteLength;
        }

        while (true)
        {
            if (offset >= codestream.Length)
            {
                throw new JxlDecodingException("The codestream ends before its last frame.");
            }

            var decoded = JxlFrameDecoder.Decode(codestream, offset, headers, state);
            offset += decoded.ByteLength;
            var header = decoded.Header;
            bool last = header.IsLast;
            bool visible = (last || header.Duration > 0) && header.FrameType is JxlFrameType.Regular or JxlFrameType.SkipProgressive;
            JxlDecodedFrame shown;
            try
            {
                shown = JxlFrameCompositor.Compose(decoded, headers, state);
            }
            catch
            {
                decoded.Dispose();
                throw;
            }

            if (visible)
            {
                yield return (shown, header.Duration);
            }
            else
            {
                shown.Dispose();
            }

            if (last)
            {
                yield break;
            }
        }
    }

    private static Image Finish(Image image, JxlContainer container, JxlCodestreamHeaders headers)
    {
        if (headers.IccProfile is { } icc)
        {
            image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = icc });
        }

        if (container.Exif is { } exif)
        {
            image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = exif });
        }

        if (container.Xmp is { } xmp)
        {
            image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Xmp, Data = xmp });
        }

        return image;
    }

    internal static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > JxlDecodingLimits.MaxFileSize)
            {
                throw new JxlDecodingException("The file is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
