using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Shared.Parallelism;
using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Jxl.Modular;

namespace PeachImage.Formats.Jxl.Features;

/// <summary>
/// The optional frame-level tools declared by frame flags: patches, splines and noise. They are read at the start of the
/// frame's global section and applied, in that order, to the filtered image before the colour transform.
/// </summary>
internal sealed class JxlFrameFeatures
{
    public JxlPatches? Patches { get; private set; }

    public JxlSplines? Splines { get; private set; }

    public JxlNoiseParams? Noise { get; private set; }

    public bool HasAny => Patches is { HasAny: true } || Splines is not null || Noise is { HasAny: true };

    public static JxlFrameFeatures Read(ref JxlBitReader br, JxlFrameHeader frame, JxlImageMetadata metadata, JxlDecoderState state)
    {
        var result = new JxlFrameFeatures();
        var dims = frame.Dimensions;
        if ((frame.Flags & JxlFrameFlags.Patches) != 0)
        {
            result.Patches = JxlPatches.Read(ref br, dims.XSizePadded, dims.YSizePadded, metadata.ExtraChannels.Count, state);
        }

        if ((frame.Flags & JxlFrameFlags.Splines) != 0)
        {
            result.Splines = JxlSplines.Read(ref br, (long)dims.XSize * dims.YSize);
        }

        if ((frame.Flags & JxlFrameFlags.Noise) != 0)
        {
            result.Noise = JxlNoiseParams.Read(ref br);
        }

        return result;
    }

    /// <summary>
    /// Applies patches and splines to three float planes (<paramref name="stride"/> floats per row) holding the frame's colour in
    /// its own colour space, at the frame's coded (pre-upsampling) resolution. Extra channels are taken from, and changes kept in,
    /// <paramref name="extras"/>.
    /// </summary>
    public void ApplyBeforeUpsampling(
        JxlImageMetadata metadata,
        JxlDecoderState state,
        float[][] planes,
        int stride,
        int width,
        int height,
        FrameExtraChannels extras,
        float yToX,
        float yToB)
    {
        if (Patches is { HasAny: true } patches)
        {
            var all = new float[3 + extras.Count][];
            planes.AsSpan(0, 3).CopyTo(all);
            for (int i = 0; i < extras.Count; i++)
            {
                all[3 + i] = extras.GetFloat(i);
            }

            patches.Apply(all, stride, width, height, metadata.ExtraChannels, state);
            extras.MarkModified();
        }

        if (Splines is { } splines)
        {
            splines.Prepare(width, height, yToX, yToB);
            splines.AddTo(planes, stride, width, height);
        }
    }

    /// <summary>Adds the noise layer to the three planes, at the frame's final (upsampled) resolution.</summary>
    public void ApplyNoise(JxlFrameHeader frame, JxlDecoderState state, float[][] planes, int stride, int width, int height, float yToX, float yToB)
    {
        if (Noise is { } noise)
        {
            JxlNoise.Add(noise, planes, stride, width, height, frame.Dimensions.GroupDimension, yToX, yToB, state.VisibleFrameIndex, state.NonVisibleFrameIndex);
        }
    }

    /// <summary>Stores a reference frame (or DC frame) from the colour planes and extra channels at the given final size.</summary>
    public static JxlReferenceFrame CopyFrame(
        float[][] planes,
        int stride,
        int width,
        int height,
        FrameExtraChannels? extras,
        bool inXyb)
    {
        int count = 3 + (extras?.Count ?? 0);
        var copy = new float[count][];
        for (int c = 0; c < count; c++)
        {
            float[] source;
            int sourceStride;
            if (c < 3)
            {
                source = planes[c];
                sourceStride = stride;
            }
            else
            {
                (source, sourceStride) = extras!.GetPlane(c - 3);
            }

            copy[c] = new float[width * height];
            for (int y = 0; y < height; y++)
            {
                Array.Copy(source, y * sourceStride, copy[c], y * width, width);
            }
        }

        return new JxlReferenceFrame(width, height, copy, inXyb);
    }
}

/// <summary>
/// The extra channels (alpha and so on) of a frame as float planes, created on demand so that frames without patches,
/// upsampled channels or reference saving never pay for the conversion. Changes are written back to the integer channels.
/// </summary>
internal sealed class FrameExtraChannels
{
    private readonly ModularImage _image;
    private readonly int _first;
    private readonly IReadOnlyList<JxlExtraChannelInfo> _info;
    private readonly int _stride;
    private readonly Plane?[] _planes;
    private bool _modified;

    public FrameExtraChannels(ModularImage image, int firstChannel, IReadOnlyList<JxlExtraChannelInfo> info, int stride)
    {
        _image = image;
        _first = firstChannel;
        _info = info;
        _stride = stride;
        _planes = new Plane?[info.Count];
    }

    private sealed class Plane(float[] data, int width, int height, int stride)
    {
        public float[] Data { get; set; } = data;

        public int Width { get; set; } = width;

        public int Height { get; set; } = height;

        public int Stride { get; set; } = stride;
    }

    public int Count => _info.Count;

    private Plane Ensure(int index)
    {
        if (_planes[index] is { } existing)
        {
            return existing;
        }

        var depth = _info[index].BitDepth;
        var channel = _image.Channels[_first + index];
        int stride = Math.Max(_stride, channel.Width);
        var data = new float[stride * channel.Height];
        var source = channel.Array;
        int channelWidth = channel.Width;
        if (depth.IsFloat)
        {
            var (bits, exponentBits) = JxlCustomFloat.Parameters(depth);
            RowParallel.For(channel.Height, y =>
            {
                for (int x = 0; x < channelWidth; x++)
                {
                    data[(y * stride) + x] = JxlCustomFloat.Decode(source[(y * channelWidth) + x], bits, exponentBits);
                }
            });
        }
        else
        {
            float scale = JxlSampleConversion.NormalizationScale((int)depth.BitsPerSample);
            RowParallel.For(channel.Height, y => JxlSampleConversion.ToFloat(source.AsSpan(y * channelWidth, channelWidth), data.AsSpan(y * stride, channelWidth), scale));
        }

        var plane = new Plane(data, channel.Width, channel.Height, stride);
        _planes[index] = plane;
        return plane;
    }

    /// <summary>The channel's float plane at the colour planes' stride (as patches need it).</summary>
    public float[] GetFloat(int index) => Ensure(index).Data;

    /// <summary>The channel's float plane and its row stride.</summary>
    public (float[] Data, int Stride) GetPlane(int index)
    {
        var plane = Ensure(index);
        return (plane.Data, plane.Stride);
    }

    public void MarkModified() => _modified = true;

    /// <summary>Upsamples one channel by <c>2^shift</c>; its new rows are <paramref name="dstStride"/> floats apart.</summary>
    public void Upsample(int index, int shift, float[] kernels, int dstStride)
    {
        var plane = Ensure(index);
        plane.Data = JxlUpsampling.Upsample(plane.Data, plane.Stride, plane.Width, plane.Height, shift, kernels, Math.Max(dstStride, plane.Width << shift));
        plane.Stride = Math.Max(dstStride, plane.Width << shift);
        plane.Width <<= shift;
        plane.Height <<= shift;
        _modified = true;
    }

    /// <summary>Writes modified float planes back into the integer channels, cropped to the final frame size.</summary>
    public void Commit(int finalWidth, int finalHeight)
    {
        if (!_modified)
        {
            return;
        }

        for (int i = 0; i < _planes.Length; i++)
        {
            if (_planes[i] is not { } plane)
            {
                continue;
            }

            var depth = _info[i].BitDepth;
            var old = _image.Channels[_first + i];
            ModularChannel channel = old;
            if (plane.Width != old.Width || plane.Height != old.Height)
            {
                channel = new ModularChannel(Math.Min(finalWidth, plane.Width), Math.Min(finalHeight, plane.Height));
                _image.Channels[_first + i] = channel;
                old.Dispose();
            }

            JxlSampleConversion.ToIntChannel(plane.Data, plane.Stride, channel.Array, channel.Width, channel.Height, depth);
        }

        _modified = false;
    }
}
