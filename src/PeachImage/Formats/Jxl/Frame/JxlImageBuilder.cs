using System.Runtime.InteropServices;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Jxl.Internal;
using PeachImage.Formats.Jxl.Modular;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.Frame;

/// <summary>
/// Converts the integer channels of a decoded Modular frame into a PeachImage <see cref="Image"/>: scaling each
/// channel's declared bit depth to the output sample width, combining colour with the alpha channel, and applying
/// the codestream's orientation.
/// </summary>
internal static class JxlImageBuilder
{
    public static Image Build(JxlDecodedFrame frame, JxlCodestreamHeaders headers, PixelFormat? target)
    {
        var metadata = headers.Metadata;
        if (JxlPixelFormatSelector.IsCmyk(metadata))
        {
            if (target is not null && target != PixelFormat.Cmyk32)
            {
                throw new JxlUnsupportedFeatureException("A CMYK JPEG XL image can only be decoded to the CMYK pixel format (use Image.ConvertToSrgb for colour management).");
            }

            return BuildCmyk(frame, headers);
        }

        var format = target ?? JxlPixelFormatSelector.Select(metadata);
        var layout = OutputLayout.From(format);
        var floatPlanes = frame.ColorPlanes;
        bool sourceGray = floatPlanes is not null
            ? metadata.ColorEncoding.ColorSpace == JxlColorSpace.Gray
            : frame.ColorChannelCount == 1;
        if (layout.Channels < 3 && !sourceGray)
        {
            throw new JxlUnsupportedFeatureException("Converting a colour JPEG XL image to a grayscale pixel format is not supported.");
        }

        int width = floatPlanes is not null ? frame.Width : frame.Image.Channels[0].Width;
        int height = floatPlanes is not null ? frame.Height : frame.Image.Channels[0].Height;
        for (int c = 0; floatPlanes is null && c < frame.ColorChannelCount; c++)
        {
            var channel = frame.Image.Channels[c];
            if (channel.Width != width || channel.Height != height)
            {
                throw new JxlUnsupportedFeatureException("Subsampled colour channels are not supported.");
            }
        }

        var colorConverter = new SampleConverter(metadata.BitDepth);
        int alphaIndex = metadata.AlphaChannelIndex;
        SampleConverter alphaConverter = default;
        ModularChannel? alpha = null;
        // Colour that was stored multiplied by alpha is returned straight (unassociated), like every other PeachImage codec.
        bool premultiplied = alphaIndex >= 0 && metadata.ExtraChannels[alphaIndex].AlphaAssociated;
        if (alphaIndex >= 0 && (layout.HasAlpha || premultiplied))
        {
            alpha = frame.Image.Channels[frame.ColorChannelCount + alphaIndex];
            alphaConverter = new SampleConverter(metadata.ExtraChannels[alphaIndex].BitDepth);
            if (alpha.Width != width || alpha.Height != height)
            {
                throw new JxlUnsupportedFeatureException("A subsampled alpha channel is not supported.");
            }
        }

        // Spot colours are rendered onto the colour channels (the reference decoder does this by default).
        var spots = new List<SpotLayer>();
        for (int i = 0; i < metadata.ExtraChannels.Count; i++)
        {
            var info = metadata.ExtraChannels[i];
            if (info.Type == JxlExtraChannelType.SpotColor && info.SpotColor is { Length: 4 } color)
            {
                var channel = frame.Image.Channels[frame.ColorChannelCount + i];
                if (channel.Width != width || channel.Height != height)
                {
                    throw new JxlUnsupportedFeatureException("A subsampled spot colour channel is not supported.");
                }

                spots.Add(new SpotLayer(channel, new SampleConverter(info.BitDepth), color));
            }
        }

        var image = Image.Create(width, height, format);
        ModularChannel? c0 = null, c1 = null, c2 = null;
        if (floatPlanes is null)
        {
            c0 = frame.Image.Channels[0];
            c1 = frame.ColorChannelCount == 3 ? frame.Image.Channels[1] : c0;
            c2 = frame.ColorChannelCount == 3 ? frame.Image.Channels[2] : c0;
        }

        RowParallel.For(height, y =>
        {
            var row = image.GetRowSpan(y);
            ReadOnlySpan<int> ra = alpha is null ? default : alpha.ReadOnlyRow(y);
            if ((premultiplied && !ra.IsEmpty) || spots.Count > 0)
            {
                WriteProcessedRow(row, y, width, layout, ra, premultiplied, spots, alphaConverter, colorConverter, frame, floatPlanes, sourceGray, c0, c1, c2);
                return;
            }

            if (floatPlanes is not null)
            {
                int offset = y * frame.ColorPlaneStride;
                var f0 = floatPlanes[0].AsSpan(offset, width);
                var f1 = sourceGray ? f0 : floatPlanes[1].AsSpan(offset, width);
                var f2 = sourceGray ? f0 : floatPlanes[2].AsSpan(offset, width);
                switch (layout.Tier)
                {
                    case 0:
                        WriteRow8(row, f0, f1, f2, ra, layout, alphaConverter);
                        break;
                    case 1:
                        WriteRow16(MemoryMarshal.Cast<byte, ushort>(row), f0, f1, f2, ra, layout, alphaConverter);
                        break;
                    default:
                        WriteRowFloat(MemoryMarshal.Cast<byte, float>(row), f0, f1, f2, ra, layout, alphaConverter);
                        break;
                }

                return;
            }

            var r0 = c0!.ReadOnlyRow(y);
            var r1 = c1!.ReadOnlyRow(y);
            var r2 = c2!.ReadOnlyRow(y);
            switch (layout.Tier)
            {
                case 0:
                    WriteRow8(row, r0, r1, r2, ra, layout, colorConverter, alphaConverter);
                    break;
                case 1:
                    WriteRow16(MemoryMarshal.Cast<byte, ushort>(row), r0, r1, r2, ra, layout, colorConverter, alphaConverter);
                    break;
                default:
                    WriteRowFloat(MemoryMarshal.Cast<byte, float>(row), r0, r1, r2, ra, layout, colorConverter, alphaConverter);
                    break;
            }
        });

        image.HasAlpha = alpha is not null;
        return image;
    }

    // CMYK: cyan, magenta and yellow are the colour channels and key is the black extra channel; the ICC profile (attached by the
    // caller) gives the values their meaning.
    private static Image BuildCmyk(JxlDecodedFrame frame, JxlCodestreamHeaders headers)
    {
        var metadata = headers.Metadata;
        var floatPlanes = frame.ColorPlanes;
        if (floatPlanes is null && frame.ColorChannelCount != 3)
        {
            throw new JxlUnsupportedFeatureException("CMYK images must be coded without the XYB colour transform.");
        }

        int blackIndex = -1;
        for (int i = 0; i < metadata.ExtraChannels.Count; i++)
        {
            if (metadata.ExtraChannels[i].Type == JxlExtraChannelType.Black)
            {
                blackIndex = i;
                break;
            }
        }

        var black = frame.Image.Channels[frame.ColorChannelCount + blackIndex];
        var c0 = floatPlanes is null ? frame.Image.Channels[0] : null;
        var c1 = floatPlanes is null ? frame.Image.Channels[1] : null;
        var c2 = floatPlanes is null ? frame.Image.Channels[2] : null;
        int width = floatPlanes is null ? c0!.Width : frame.Width;
        int height = floatPlanes is null ? c0!.Height : frame.Height;
        if (black.Width != width || black.Height != height)
        {
            throw new JxlUnsupportedFeatureException("A subsampled black channel is not supported.");
        }

        var colorConverter = new SampleConverter(metadata.BitDepth);
        var blackConverter = new SampleConverter(metadata.ExtraChannels[blackIndex].BitDepth);
        var image = Image.Create(width, height, PixelFormat.Cmyk32);
        RowParallel.For(height, y =>
        {
            var row = image.GetRowSpan(y);
            var rk = black.ReadOnlyRow(y);
            for (int x = 0; x < width; x++)
            {
                int o = x * 4;
                if (floatPlanes is not null)
                {
                    int index = (y * frame.ColorPlaneStride) + x;
                    row[o] = Ink(FloatToByte(floatPlanes[0][index]));
                    row[o + 1] = Ink(FloatToByte(floatPlanes[1][index]));
                    row[o + 2] = Ink(FloatToByte(floatPlanes[2][index]));
                }
                else
                {
                    row[o] = Ink(colorConverter.ToByte(c0!.ReadOnlyRow(y)[x]));
                    row[o + 1] = Ink(colorConverter.ToByte(c1!.ReadOnlyRow(y)[x]));
                    row[o + 2] = Ink(colorConverter.ToByte(c2!.ReadOnlyRow(y)[x]));
                }

                row[o + 3] = Ink(blackConverter.ToByte(rk[x]));
            }
        });

        image.HasAlpha = false;
        return image;
    }

    private static byte Ink(byte sample) => (byte)(255 - sample);

    private sealed record SpotLayer(ModularChannel Channel, SampleConverter Converter, float[] Color);

    // Rows that need float arithmetic: spot colours are mixed in, then colour stored multiplied by alpha is divided by it
    // (libjxl: multiplier 1 / max(alpha, 2^-26)); the result goes through the float row writers.
    private static void WriteProcessedRow(
        Span<byte> row,
        int y,
        int width,
        OutputLayout layout,
        ReadOnlySpan<int> ra,
        bool premultiplied,
        List<SpotLayer> spots,
        SampleConverter alphaConverter,
        SampleConverter colorConverter,
        JxlDecodedFrame frame,
        float[][]? floatPlanes,
        bool sourceGray,
        ModularChannel? c0,
        ModularChannel? c1,
        ModularChannel? c2)
    {
        const float smallAlpha = 1f / (1u << 26);
        var t0 = new float[width];
        var t1 = new float[width];
        var t2 = new float[width];
        for (int x = 0; x < width; x++)
        {
            if (floatPlanes is not null)
            {
                int index = (y * frame.ColorPlaneStride) + x;
                t0[x] = floatPlanes[0][index];
                t1[x] = sourceGray ? t0[x] : floatPlanes[1][index];
                t2[x] = sourceGray ? t0[x] : floatPlanes[2][index];
            }
            else
            {
                t0[x] = colorConverter.ToFloat(c0!.ReadOnlyRow(y)[x]);
                t1[x] = colorConverter.ToFloat(c1!.ReadOnlyRow(y)[x]);
                t2[x] = colorConverter.ToFloat(c2!.ReadOnlyRow(y)[x]);
            }
        }

        foreach (var spot in spots)
        {
            var samples = spot.Channel.ReadOnlyRow(y);
            float solidity = spot.Color[3];
            for (int x = 0; x < width; x++)
            {
                float mix = solidity * spot.Converter.ToFloat(samples[x]);
                t0[x] = (mix * spot.Color[0]) + ((1f - mix) * t0[x]);
                t1[x] = (mix * spot.Color[1]) + ((1f - mix) * t1[x]);
                t2[x] = (mix * spot.Color[2]) + ((1f - mix) * t2[x]);
            }
        }

        if (premultiplied && !ra.IsEmpty)
        {
            for (int x = 0; x < width; x++)
            {
                float multiplier = 1f / MathF.Max(smallAlpha, alphaConverter.ToFloat(ra[x]));
                t0[x] *= multiplier;
                t1[x] *= multiplier;
                t2[x] *= multiplier;
            }
        }

        switch (layout.Tier)
        {
            case 0:
                WriteRow8(row, t0, t1, t2, ra, layout, alphaConverter);
                break;
            case 1:
                WriteRow16(MemoryMarshal.Cast<byte, ushort>(row), t0, t1, t2, ra, layout, alphaConverter);
                break;
            default:
                WriteRowFloat(MemoryMarshal.Cast<byte, float>(row), t0, t1, t2, ra, layout, alphaConverter);
                break;
        }
    }

    // Float colour (already display-referred, nominally 0..1) to 8-bit, with plain round-to-nearest.
    private static void WriteRow8(Span<byte> row, ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, ReadOnlySpan<float> r2, ReadOnlySpan<int> ra, OutputLayout layout, SampleConverter alphaConv)
    {
        int channels = layout.Channels;
        for (int x = 0; x < r0.Length; x++)
        {
            int o = x * channels;
            row[o] = FloatToByte(r0[x]);
            if (channels == 1)
            {
                continue;
            }

            row[o + 1] = FloatToByte(r1[x]);
            row[o + 2] = FloatToByte(r2[x]);
            if (channels == 4)
            {
                row[o + 3] = ra.IsEmpty ? (byte)255 : alphaConv.ToByte(ra[x]);
            }
        }
    }

    private static void WriteRow16(Span<ushort> row, ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, ReadOnlySpan<float> r2, ReadOnlySpan<int> ra, OutputLayout layout, SampleConverter alphaConv)
    {
        int channels = layout.Channels;
        for (int x = 0; x < r0.Length; x++)
        {
            int o = x * channels;
            row[o] = FloatToUInt16(r0[x]);
            if (channels == 1)
            {
                continue;
            }

            row[o + 1] = FloatToUInt16(r1[x]);
            row[o + 2] = FloatToUInt16(r2[x]);
            if (channels == 4)
            {
                row[o + 3] = ra.IsEmpty ? ushort.MaxValue : alphaConv.ToUInt16(ra[x]);
            }
        }
    }

    private static void WriteRowFloat(Span<float> row, ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, ReadOnlySpan<float> r2, ReadOnlySpan<int> ra, OutputLayout layout, SampleConverter alphaConv)
    {
        int channels = layout.Channels;
        for (int x = 0; x < r0.Length; x++)
        {
            int o = x * channels;
            row[o] = float.IsNaN(r0[x]) ? 0f : r0[x];
            if (channels == 1)
            {
                continue;
            }

            row[o + 1] = float.IsNaN(r1[x]) ? 0f : r1[x];
            row[o + 2] = float.IsNaN(r2[x]) ? 0f : r2[x];
            if (channels == 4)
            {
                row[o + 3] = ra.IsEmpty ? 1f : alphaConv.ToFloat(ra[x]);
            }
        }
    }

    private static byte FloatToByte(float v) => float.IsNaN(v) ? (byte)0 : (byte)(int)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    private static ushort FloatToUInt16(float v) => float.IsNaN(v) ? (ushort)0 : (ushort)(int)(Math.Clamp(v, 0f, 1f) * 65535f + 0.5f);

    private static void WriteRow8(Span<byte> row, ReadOnlySpan<int> r0, ReadOnlySpan<int> r1, ReadOnlySpan<int> r2, ReadOnlySpan<int> ra, OutputLayout layout, SampleConverter color, SampleConverter alphaConv)
    {
        int channels = layout.Channels;
        for (int x = 0; x < r0.Length; x++)
        {
            int o = x * channels;
            if (channels == 1)
            {
                row[o] = color.ToByte(r0[x]);
                continue;
            }

            row[o] = color.ToByte(r0[x]);
            row[o + 1] = color.ToByte(r1[x]);
            row[o + 2] = color.ToByte(r2[x]);
            if (channels == 4)
            {
                row[o + 3] = ra.IsEmpty ? (byte)255 : alphaConv.ToByte(ra[x]);
            }
        }
    }

    private static void WriteRow16(Span<ushort> row, ReadOnlySpan<int> r0, ReadOnlySpan<int> r1, ReadOnlySpan<int> r2, ReadOnlySpan<int> ra, OutputLayout layout, SampleConverter color, SampleConverter alphaConv)
    {
        int channels = layout.Channels;
        for (int x = 0; x < r0.Length; x++)
        {
            int o = x * channels;
            if (channels == 1)
            {
                row[o] = color.ToUInt16(r0[x]);
                continue;
            }

            row[o] = color.ToUInt16(r0[x]);
            row[o + 1] = color.ToUInt16(r1[x]);
            row[o + 2] = color.ToUInt16(r2[x]);
            if (channels == 4)
            {
                row[o + 3] = ra.IsEmpty ? ushort.MaxValue : alphaConv.ToUInt16(ra[x]);
            }
        }
    }

    private static void WriteRowFloat(Span<float> row, ReadOnlySpan<int> r0, ReadOnlySpan<int> r1, ReadOnlySpan<int> r2, ReadOnlySpan<int> ra, OutputLayout layout, SampleConverter color, SampleConverter alphaConv)
    {
        int channels = layout.Channels;
        for (int x = 0; x < r0.Length; x++)
        {
            int o = x * channels;
            if (channels == 1)
            {
                row[o] = color.ToFloat(r0[x]);
                continue;
            }

            row[o] = color.ToFloat(r0[x]);
            row[o + 1] = color.ToFloat(r1[x]);
            row[o + 2] = color.ToFloat(r2[x]);
            if (channels == 4)
            {
                row[o + 3] = ra.IsEmpty ? 1f : alphaConv.ToFloat(ra[x]);
            }
        }
    }

    /// <summary>The sample width tier (0 = 8-bit, 1 = 16-bit, 2 = float) and channel count of a pixel format.</summary>
    private readonly record struct OutputLayout(int Tier, int Channels)
    {
        public bool HasAlpha => Channels == 4;

        public static OutputLayout From(PixelFormat format) => format switch
        {
            PixelFormat.Gray8 => new(0, 1),
            PixelFormat.Rgb24 => new(0, 3),
            PixelFormat.Rgba32 => new(0, 4),
            PixelFormat.Gray16 => new(1, 1),
            PixelFormat.Rgb48 => new(1, 3),
            PixelFormat.Rgba64 => new(1, 4),
            PixelFormat.GrayF32 => new(2, 1),
            PixelFormat.RgbF32 => new(2, 3),
            PixelFormat.RgbaF32 => new(2, 4),
            _ => throw new JxlUnsupportedFeatureException($"The pixel format {format} is not a supported JPEG XL decode target."),
        };
    }

    /// <summary>Converts integer samples of one channel's declared bit depth (or custom float format) into 8/16-bit unsigned or float samples.</summary>
    private readonly struct SampleConverter
    {
        private readonly int _bits;
        private readonly int _exponentBits;
        private readonly bool _isFloat;
        private readonly double _maxValue;

        public SampleConverter(JxlBitDepth depth)
        {
            _bits = (int)depth.BitsPerSample;
            _exponentBits = (int)depth.ExponentBitsPerSample;
            _isFloat = depth.IsFloat;
            _maxValue = _isFloat ? 1.0 : (double)((1UL << _bits) - 1);
        }

        public float ToFloat(int value)
        {
            if (_isFloat)
            {
                return JxlCustomFloat.Decode(value, _bits, _exponentBits);
            }

            return (float)(value / _maxValue);
        }

        public byte ToByte(int value)
        {
            if (!_isFloat && _bits == 8)
            {
                return (byte)Math.Clamp(value, 0, 255);
            }

            return (byte)Math.Clamp((int)((Unit(value) * 255.0) + 0.5), 0, 255);
        }

        public ushort ToUInt16(int value)
        {
            if (!_isFloat && _bits == 16)
            {
                return (ushort)Math.Clamp(value, 0, 65535);
            }

            return (ushort)Math.Clamp((int)((Unit(value) * 65535.0) + 0.5), 0, 65535);
        }

        private double Unit(int value)
        {
            double unit = _isFloat ? JxlCustomFloat.Decode(value, _bits, _exponentBits) : value / _maxValue;
            return double.IsNaN(unit) ? 0 : Math.Clamp(unit, 0.0, 1.0);
        }
    }
}
