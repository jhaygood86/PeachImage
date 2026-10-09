using PeachImage.Formats.Jxl.Features;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Jxl.Modular;

namespace PeachImage.Formats.Jxl.Frame;

/// <summary>
/// Places a decoded frame on the image canvas: blends a cropped or non-replacing frame over the reference frame it names
/// (in the output colour encoding, as the reference decoder does) and keeps the result as a reference when later frames
/// may refer to it.
/// </summary>
internal static class JxlFrameCompositor
{
    /// <summary>Whether the frame must be combined with a background rather than simply being the canvas.</summary>
    public static bool NeedsBlending(JxlFrameHeader header)
    {
        if (header.FrameType is not (JxlFrameType.Regular or JxlFrameType.SkipProgressive))
        {
            return false;
        }

        if (header.CustomSizeOrOrigin || header.Blending.Mode != JxlBlendMode.Replace)
        {
            return true;
        }

        foreach (var info in header.ExtraChannelBlending)
        {
            if (info.Mode != JxlBlendMode.Replace)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the frame to show for <paramref name="frame"/>. Either <paramref name="frame"/> itself, or (when it had to be
    /// blended) a new full-canvas frame, in which case <paramref name="frame"/> is disposed.
    /// </summary>
    public static JxlDecodedFrame Compose(JxlDecodedFrame frame, JxlCodestreamHeaders headers, JxlDecoderState state)
    {
        var header = frame.Header;
        bool blend = NeedsBlending(header);
        bool save = header.CanBeReferenced && !header.SaveBeforeColorTransform;
        if (!blend && !save)
        {
            return frame;
        }

        var metadata = headers.Metadata;
        float[][] planes = ToFloatPlanes(frame, metadata, out int frameWidth, out int frameHeight);
        int canvasWidth = frameWidth;
        int canvasHeight = frameHeight;
        if (blend)
        {
            canvasWidth = checked((int)headers.Size.Width);
            canvasHeight = checked((int)headers.Size.Height);
            planes = Blend(header, headers, state, planes, frameWidth, frameHeight, canvasWidth, canvasHeight);
        }

        if (save)
        {
            state.References[(int)header.SaveAsReference] = new JxlReferenceFrame(canvasWidth, canvasHeight, planes, isInXyb: false);
        }

        if (!blend)
        {
            return frame;
        }

        var result = BuildCanvasFrame(frame, metadata, planes, canvasWidth, canvasHeight);
        frame.Dispose();
        return result;
    }

    // Colour planes then extra channels as compact float planes of the frame's size (display-encoded colour).
    private static float[][] ToFloatPlanes(JxlDecodedFrame frame, JxlImageMetadata metadata, out int width, out int height)
    {
        width = frame.Header.Dimensions.XSizeUpsampled;
        height = frame.Header.Dimensions.YSizeUpsampled;
        int numEc = metadata.ExtraChannels.Count;
        var planes = new float[3 + numEc][];
        if (frame.ColorPlanes is { } colorPlanes)
        {
            for (int c = 0; c < 3; c++)
            {
                planes[c] = new float[width * height];
                for (int y = 0; y < height; y++)
                {
                    Array.Copy(colorPlanes[c], y * frame.ColorPlaneStride, planes[c], y * width, width);
                }
            }
        }
        else
        {
            for (int c = 0; c < frame.ColorChannelCount; c++)
            {
                var channel = frame.Image.Channels[c];
                if (channel.Width != width || channel.Height != height)
                {
                    throw new JxlUnsupportedFeatureException("Subsampled colour channels are not supported.");
                }

                planes[c] = new float[width * height];
                JxlSampleConversion.ToFloatPlane(channel.Array, planes[c], width, height, metadata.BitDepth);
            }

            for (int c = frame.ColorChannelCount; c < 3; c++)
            {
                planes[c] = planes[0];
            }
        }

        for (int i = 0; i < numEc; i++)
        {
            var depth = metadata.ExtraChannels[i].BitDepth;
            var channel = frame.Image.Channels[frame.ColorChannelCount + i];
            if (channel.Width != width || channel.Height != height)
            {
                throw new JxlUnsupportedFeatureException("Subsampled extra channels are not supported.");
            }

            var plane = new float[width * height];
            JxlSampleConversion.ToFloatPlane(channel.Array, plane, width, height, depth);

            planes[3 + i] = plane;
        }

        return planes;
    }

    private static float[][] Blend(
        JxlFrameHeader header,
        JxlCodestreamHeaders headers,
        JxlDecoderState state,
        float[][] foreground,
        int frameWidth,
        int frameHeight,
        int canvasWidth,
        int canvasHeight)
    {
        var metadata = headers.Metadata;
        int numEc = metadata.ExtraChannels.Count;
        var canvas = new float[3 + numEc][];
        for (int c = 0; c < canvas.Length; c++)
        {
            int slot = (int)(c < 3 ? header.Blending.Source : header.ExtraChannelBlending[c - 3].Source);
            var reference = state.References[slot];
            if (reference is null)
            {
                canvas[c] = new float[canvasWidth * canvasHeight];
                continue;
            }

            if (reference.IsInXyb)
            {
                throw new JxlDecodingException("A frame is blended onto a reference frame that was saved before the colour transform.");
            }

            if (reference.Width != canvasWidth || reference.Height != canvasHeight)
            {
                throw new JxlUnsupportedFeatureException("Blending onto a reference frame that is not the size of the image is not supported.");
            }

            canvas[c] = (float[])reference.Planes[c].Clone();
        }

        var colorBlending = ToBlending(header.Blending);
        var extraBlending = new JxlPatchBlending[numEc];
        for (int i = 0; i < numEc; i++)
        {
            extraBlending[i] = ToBlending(header.ExtraChannelBlending[i]);
        }

        int originX = header.OriginX;
        int originY = header.OriginY;
        int startX = Math.Max(0, originX);
        int endX = Math.Min(canvasWidth, originX + frameWidth);
        if (endX <= startX)
        {
            return canvas;
        }

        for (int y = 0; y < frameHeight; y++)
        {
            int canvasY = originY + y;
            if (canvasY < 0 || canvasY >= canvasHeight)
            {
                continue;
            }

            JxlBlending.BlendRun(
                canvas,
                (canvasY * canvasWidth) + startX,
                foreground,
                (y * frameWidth) + (startX - originX),
                endX - startX,
                colorBlending,
                extraBlending,
                metadata.ExtraChannels);
        }

        return canvas;
    }

    private static JxlPatchBlending ToBlending(JxlBlendingInfo info) => new(
        info.Mode switch
        {
            JxlBlendMode.Replace => JxlPatchBlendMode.Replace,
            JxlBlendMode.Add => JxlPatchBlendMode.Add,
            JxlBlendMode.Mul => JxlPatchBlendMode.Mul,
            JxlBlendMode.Blend => JxlPatchBlendMode.BlendAbove,
            _ => JxlPatchBlendMode.AlphaWeightedAddAbove,
        },
        (int)info.AlphaChannel,
        info.Clamp);

    // A decoded frame whose colour is the float canvas and whose extra channels are re-quantized integer channels.
    private static JxlDecodedFrame BuildCanvasFrame(JxlDecodedFrame source, JxlImageMetadata metadata, float[][] planes, int width, int height)
    {
        var image = new ModularImage(width, height, checked((int)metadata.BitDepth.BitsPerSample));
        try
        {
            for (int i = 0; i < metadata.ExtraChannels.Count; i++)
            {
                var channel = new ModularChannel(width, height);
                image.Channels.Add(channel);
                JxlSampleConversion.ToIntChannel(planes[3 + i], width, channel.Array, width, height, metadata.ExtraChannels[i].BitDepth);
            }

            return new JxlDecodedFrame(source.Header, image, 0)
            {
                ColorPlanes = [planes[0], planes[1], planes[2]],
                ColorPlaneStride = width,
                ByteLength = source.ByteLength,
                CanvasWidth = width,
                CanvasHeight = height,
            };
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }
}
