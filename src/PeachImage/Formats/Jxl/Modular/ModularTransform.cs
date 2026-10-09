using System.Buffers;
using System.Numerics;
using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.Modular;

internal enum ModularTransformId : uint
{
    Rct = 0,
    Palette = 1,
    Squeeze = 2,
    Invalid = 3,
}

/// <summary>One squeeze (Haar-style wavelet) step.</summary>
internal readonly record struct SqueezeParams(bool Horizontal, bool InPlace, uint BeginChannel, uint NumChannels)
{
    public static SqueezeParams Read(ref JxlBitReader br)
    {
        bool horizontal = br.ReadBool();
        bool inPlace = br.ReadBool();
        uint begin = JxlFieldReader.ReadU32(ref br, U32Dist.Bits(3), U32Dist.BitsOffset(6, 8), U32Dist.BitsOffset(10, 72), U32Dist.BitsOffset(13, 1096));
        uint num = JxlFieldReader.ReadU32(ref br, U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3), U32Dist.BitsOffset(4, 4));
        return new SqueezeParams(horizontal, inPlace, begin, num);
    }
}

/// <summary>
/// A Modular transform applied by the encoder (reversible colour transform, palette or squeeze). The stream describes
/// the transformed channels; <see cref="MetaApply"/> reshapes the channel list to match, and <see cref="Inverse"/>
/// reconstructs the original channels after decoding.
/// </summary>
internal sealed class ModularTransform
{
    private ModularTransform()
    {
    }

    public ModularTransformId Id { get; private set; }

    /// <summary>First channel (RCT, palette).</summary>
    public uint BeginChannel { get; private set; }

    public uint RctType { get; private set; } = 6;

    /// <summary>Number of channels a palette covers.</summary>
    public uint NumChannels { get; private set; } = 3;

    public uint NbColors { get; private set; } = 256;

    public uint NbDeltas { get; private set; }

    public ModularPredictor Predictor { get; private set; } = ModularPredictor.Zero;

    /// <summary>Squeeze steps; empty means the default sequence.</summary>
    public List<SqueezeParams> Squeezes { get; private set; } = [];

    public static ModularTransform Read(ref JxlBitReader br)
    {
        var t = new ModularTransform
        {
            Id = (ModularTransformId)JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3)),
        };
        if (t.Id == ModularTransformId.Invalid)
        {
            throw new JxlDecodingException("Invalid transform id.");
        }

        if (t.Id is ModularTransformId.Rct or ModularTransformId.Palette)
        {
            t.BeginChannel = JxlFieldReader.ReadU32(ref br, U32Dist.Bits(3), U32Dist.BitsOffset(6, 8), U32Dist.BitsOffset(10, 72), U32Dist.BitsOffset(13, 1096));
        }

        if (t.Id == ModularTransformId.Rct)
        {
            t.RctType = JxlFieldReader.ReadU32(ref br, U32Dist.Val(6), U32Dist.Bits(2), U32Dist.BitsOffset(4, 2), U32Dist.BitsOffset(6, 10));
            if (t.RctType >= 42)
            {
                throw new JxlDecodingException("Invalid RCT type.");
            }
        }

        if (t.Id == ModularTransformId.Palette)
        {
            t.NumChannels = JxlFieldReader.ReadU32(ref br, U32Dist.Val(1), U32Dist.Val(3), U32Dist.Val(4), U32Dist.BitsOffset(13, 1));
            t.NbColors = JxlFieldReader.ReadU32(ref br, U32Dist.BitsOffset(8, 0), U32Dist.BitsOffset(10, 256), U32Dist.BitsOffset(12, 1280), U32Dist.BitsOffset(16, 5376));
            t.NbDeltas = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.BitsOffset(8, 1), U32Dist.BitsOffset(10, 257), U32Dist.BitsOffset(16, 1281));
            uint predictor = br.ReadBits(4);
            if (predictor >= (uint)ModularPredictor.Best)
            {
                throw new JxlDecodingException("Invalid palette predictor.");
            }

            t.Predictor = (ModularPredictor)predictor;
        }

        if (t.Id == ModularTransformId.Squeeze)
        {
            uint count = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.BitsOffset(4, 1), U32Dist.BitsOffset(6, 9), U32Dist.BitsOffset(8, 41));
            var squeezes = new List<SqueezeParams>((int)count);
            for (int i = 0; i < count; i++)
            {
                squeezes.Add(SqueezeParams.Read(ref br));
            }

            t.Squeezes = squeezes;
        }

        br.ThrowIfOverrun();
        return t;
    }

    /// <summary>Reshapes <paramref name="image"/>'s channel list from the original layout to the transformed one.</summary>
    public void MetaApply(ModularImage image)
    {
        switch (Id)
        {
            case ModularTransformId.Rct:
                CheckEqualChannels(image, (int)BeginChannel, (int)BeginChannel + 2);
                break;
            case ModularTransformId.Squeeze:
                MetaSqueeze(image, Squeezes);
                break;
            case ModularTransformId.Palette:
                MetaPalette(image, (int)BeginChannel, (int)(BeginChannel + NumChannels - 1), NbColors, NbDeltas);
                break;
            default:
                throw new JxlDecodingException("Unknown transform.");
        }
    }

    /// <summary>Reconstructs the original channels from the decoded, transformed ones.</summary>
    public void Inverse(ModularImage image, WeightedPredictorHeader wpHeader)
    {
        switch (Id)
        {
            case ModularTransformId.Rct:
                InverseRct(image, (int)BeginChannel, (int)RctType);
                break;
            case ModularTransformId.Squeeze:
                InverseSqueeze(image, Squeezes);
                break;
            case ModularTransformId.Palette:
                InversePalette(image, (int)BeginChannel, NbDeltas, Predictor, wpHeader);
                break;
            default:
                throw new JxlDecodingException("Unknown transform.");
        }
    }

    // ---- Shared validation ----

    private static void CheckEqualChannels(ModularImage image, int c1, int c2)
    {
        if (c1 < 0 || c1 > image.Channels.Count || c2 >= image.Channels.Count || c2 < c1)
        {
            throw new JxlDecodingException($"Invalid channel range {c1}..{c2} (there are {image.Channels.Count} channels).");
        }

        if (c1 < image.MetaChannelCount && c2 >= image.MetaChannelCount)
        {
            throw new JxlDecodingException("Invalid transform of a mix of meta and non-meta channels.");
        }

        var first = image.Channels[c1];
        for (int c = c1 + 1; c <= c2; c++)
        {
            if (!first.SameGeometry(image.Channels[c]))
            {
                throw new JxlDecodingException("Transformed channels have different geometry.");
            }
        }
    }

    // ---- RCT ----

    private static void InverseRct(ModularImage image, int begin, int rctType)
    {
        CheckEqualChannels(image, begin, begin + 2);
        if (rctType == 0)
        {
            return;
        }

        // Permutation: 0=RGB, 1=GBR, 2=BRG, 3=RBG, 4=GRB, 5=BGR.
        int permutation = rctType / 7;
        int custom = rctType % 7;
        int out0 = begin + (permutation % 3);
        int out1 = begin + ((permutation + 1 + (permutation / 3)) % 3);
        int out2 = begin + ((permutation + 2 - (permutation / 3)) % 3);

        var channels = image.Channels;
        if (custom == 0)
        {
            // Permute only.
            var c0 = channels[begin];
            var c1 = channels[begin + 1];
            var c2 = channels[begin + 2];
            channels[out0] = c0;
            channels[out1] = c1;
            channels[out2] = c2;
            return;
        }

        int height = channels[begin].Height;
        var ch0 = channels[begin];
        var ch1 = channels[begin + 1];
        var ch2 = channels[begin + 2];
        var oc0 = channels[out0];
        var oc1 = channels[out1];
        var oc2 = channels[out2];
        RowParallel.For(height, y => InverseRctRow(ch0.Row(y), ch1.Row(y), ch2.Row(y), oc0.Row(y), oc1.Row(y), oc2.Row(y), custom));
    }

    // One row of the inverse RCT; the output rows may alias the input rows (position for position).
    private static void InverseRctRow(Span<int> in0, Span<int> in1, Span<int> in2, Span<int> o0, Span<int> o1, Span<int> o2, int custom)
    {
        int width = in0.Length;
        int second = custom >> 1;
        int third = custom & 1;
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; x <= width - Vector<int>.Count; x += Vector<int>.Count)
            {
                var a = new Vector<int>(in0[x..]);
                var b = new Vector<int>(in1[x..]);
                var c = new Vector<int>(in2[x..]);
                if (custom == 6)
                {
                    // YCoCg.
                    var tmp = a - Vector.ShiftRightArithmetic(c, 1);
                    var blue = tmp - Vector.ShiftRightArithmetic(b, 1);
                    (blue + b).CopyTo(o0[x..]);
                    (c + tmp).CopyTo(o1[x..]);
                    blue.CopyTo(o2[x..]);
                }
                else
                {
                    if (third != 0)
                    {
                        c += a;
                    }

                    if (second == 1)
                    {
                        b += a;
                    }
                    else if (second == 2)
                    {
                        b += Vector.ShiftRightArithmetic(a + c, 1);
                    }

                    a.CopyTo(o0[x..]);
                    b.CopyTo(o1[x..]);
                    c.CopyTo(o2[x..]);
                }
            }
        }

        for (; x < width; x++)
        {
            int a = in0[x];
            int b = in1[x];
            int c = in2[x];
            if (custom == 6)
            {
                int tmp = a - (c >> 1);
                int g = c + tmp;
                int blue = tmp - (b >> 1);
                int red = blue + b;
                o0[x] = red;
                o1[x] = g;
                o2[x] = blue;
            }
            else
            {
                if (third != 0)
                {
                    c += a;
                }

                if (second == 1)
                {
                    b += a;
                }
                else if (second == 2)
                {
                    b += (a + c) >> 1;
                }

                o0[x] = a;
                o1[x] = b;
                o2[x] = c;
            }
        }
    }

    // ---- Palette ----

    private static void MetaPalette(ModularImage image, int begin, int end, uint nbColors, uint nbDeltas)
    {
        CheckEqualChannels(image, begin, end);
        int nb = end - begin + 1;
        if (begin >= image.MetaChannelCount)
        {
            image.MetaChannelCount++;
        }
        else
        {
            if (end >= image.MetaChannelCount)
            {
                throw new JxlDecodingException("Invalid palette over meta channels.");
            }

            image.MetaChannelCount += 2 - nb;
        }

        for (int c = begin + 1; c <= end; c++)
        {
            image.Channels[c].Dispose();
        }

        image.Channels.RemoveRange(begin + 1, end - begin);
        image.Channels.Insert(0, new ModularChannel(checked((int)(nbColors + nbDeltas)), nb, -1, -1));
    }

    private static void InversePalette(ModularImage image, int begin, uint nbDeltas, ModularPredictor predictor, WeightedPredictorHeader wpHeader)
    {
        if (image.MetaChannelCount < 1)
        {
            throw new JxlDecodingException("Palette transform without a palette.");
        }

        var channels = image.Channels;
        int nb = channels[0].Height;
        int c0 = begin + 1;
        if (c0 >= channels.Count)
        {
            throw new JxlDecodingException("Palette channel is out of range.");
        }

        int w = channels[c0].Width;
        int h = channels[c0].Height;
        if (nb < 1)
        {
            throw new JxlDecodingException("Corrupted palette transform.");
        }

        for (int i = 1; i < nb; i++)
        {
            channels.Insert(c0 + 1, new ModularChannel(w, h, channels[c0].HShift, channels[c0].VShift));
        }

        var palette = channels[0];
        int paletteWidth = palette.Width;
        int bitDepth = Math.Min(image.BitDepth, 24);
        var paletteData = palette.Samples;

        if (w == 0)
        {
            // Nothing to do; avoid touching empty channels.
        }
        else if (nbDeltas == 0 && predictor == ModularPredictor.Zero)
        {
            if (nb == 1)
            {
                for (int y = 0; y < h; y++)
                {
                    var row = channels[c0].Row(y);
                    for (int x = 0; x < w; x++)
                    {
                        int index = Math.Clamp(row[x], 0, paletteWidth - 1);
                        row[x] = PaletteValue(paletteData, index, 0, paletteWidth, paletteWidth, bitDepth);
                    }
                }
            }
            else
            {
                for (int y = 0; y < h; y++)
                {
                    var indexRow = channels[c0].Row(y);
                    for (int x = 0; x < w; x++)
                    {
                        int index = indexRow[x];
                        for (int c = 0; c < nb; c++)
                        {
                            channels[c0 + c].Row(y)[x] = PaletteValue(paletteData, index, c, paletteWidth, paletteWidth, bitDepth);
                        }
                    }
                }
            }
        }
        else
        {
            // Delta palette: some entries are deltas from a prediction; the index plane is read from a copy.
            int[] indices = ArrayPool<int>.Shared.Rent(w * h);
            try
            {
                channels[c0].Samples.CopyTo(indices);
                for (int c = 0; c < nb; c++)
                {
                    var channel = channels[c0 + c];
                    var wp = predictor == ModularPredictor.Weighted ? new WeightedPredictorState(wpHeader, channel.Width) : null;
                    var data = channel.Samples;
                    for (int y = 0; y < channel.Height; y++)
                    {
                        for (int x = 0; x < channel.Width; x++)
                        {
                            int index = indices[(y * w) + x];
                            int entry = PaletteValue(paletteData, index, c, paletteWidth, paletteWidth, bitDepth);
                            long value;
                            if (index < nbDeltas)
                            {
                                long guess = wp is null
                                    ? ModularPredictors.PredictSimple(data, channel.Width, x, y, predictor)
                                    : ModularPredictors.PredictWeighted(data, channel.Width, x, y, wp);
                                value = guess + entry;
                            }
                            else
                            {
                                value = entry;
                                if (wp is not null)
                                {
                                    // Keep the predictor state in step even for non-delta entries.
                                    ModularPredictors.PredictWeighted(data, channel.Width, x, y, wp);
                                }
                            }

                            data[(y * channel.Width) + x] = (int)value;
                            wp?.UpdateErrors(value, x, y, channel.Width);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(indices);
            }
        }

        if (c0 >= image.MetaChannelCount)
        {
            image.MetaChannelCount--;
        }
        else
        {
            image.MetaChannelCount -= 2 - nb;
        }

        channels[0].Dispose();
        channels.RemoveAt(0);
    }

    private static readonly short[] DeltaPalette =
    [
        0, 0, 0, 4, 4, 4, 11, 0, 0, 0, 0, -13, 0, -12, 0, -10, -10, -10,
        -18, -18, -18, -27, -27, -27, -18, -18, 0, 0, 0, -32, -32, 0, 0, -37, -37, -37,
        0, -32, -32, 24, 24, 45, 50, 50, 50, -45, -24, -24, -24, -45, -45, 0, -24, -24,
        -34, -34, 0, -24, 0, -24, -45, -45, -24, 64, 64, 64, -32, 0, -32, 0, -32, 0,
        -32, 0, 32, -24, -45, -24, 45, 24, 45, 24, -24, -45, -45, -24, 24, 80, 80, 80,
        64, 0, 0, 0, 0, -64, 0, -64, -64, -24, -24, 45, 96, 96, 96, 64, 64, 0,
        45, -24, -24, 34, -34, 0, 112, 112, 112, 24, -45, -45, 45, 45, -24, 0, -32, 32,
        24, -24, 45, 0, 96, 96, 45, -24, 24, 24, -45, -24, -24, -45, 24, 0, -64, 0,
        96, 0, 0, 128, 128, 128, 64, 0, 64, 144, 144, 144, 96, 96, 0, -36, -36, 36,
        45, -24, -45, 45, -45, -24, 0, 0, -96, 0, 128, 128, 0, 96, 0, 45, 24, -45,
        -128, 0, 0, 24, -45, 24, -45, 24, -45, 64, 0, -64, 64, -64, -64, 96, 0, 96,
        45, -45, 24, 24, 45, -45, 64, 64, -64, 128, 128, 0, 0, 0, -128, -24, 45, -45,
    ];

    private const int SmallCube = 4;
    private const int SmallCubeBits = 2;
    private const int LargeCube = 5;
    private const int LargeCubeOffset = SmallCube * SmallCube * SmallCube;

    // Palette index → sample: explicit entries, then implicit delta entries (negative), a 4x4x4 and a 5x5x5 colour cube.
    private static int PaletteValue(ReadOnlySpan<int> palette, int index, int c, int paletteSize, int rowStride, int bitDepth)
    {
        if (index < 0)
        {
            if (c >= 3)
            {
                return 0;
            }

            index = -(index + 1);
            index %= 1 + (2 * ((DeltaPalette.Length / 3) - 1));
            int multiplier = (index & 1) == 0 ? -1 : 1;
            int result = DeltaPalette[((((index + 1) >> 1) * 3) + c)] * multiplier;
            if (bitDepth > 8)
            {
                result *= 1 << (bitDepth - 8);
            }

            return result;
        }

        if (paletteSize <= index && index < paletteSize + LargeCubeOffset)
        {
            if (c >= 3)
            {
                return 0;
            }

            index -= paletteSize;
            index >>= c * SmallCubeBits;
            return Scale4((ulong)(index % SmallCube), bitDepth) + (1 << Math.Max(0, bitDepth - 3));
        }

        if (paletteSize + LargeCubeOffset <= index)
        {
            if (c >= 3)
            {
                return 0;
            }

            index -= paletteSize + LargeCubeOffset;
            switch (c)
            {
                case 1:
                    index /= LargeCube;
                    break;
                case 2:
                    index /= LargeCube * LargeCube;
                    break;
            }

            return Scale4((ulong)(index % LargeCube), bitDepth);
        }

        return palette[(c * rowStride) + index];
    }

    private static int Scale4(ulong value, int bitDepth) => (int)((value * ((1UL << bitDepth) - 1)) >> 2);

    // ---- Squeeze ----

    private static void DefaultSqueezeParameters(List<SqueezeParams> parameters, ModularImage image)
    {
        int numChannels = image.Channels.Count - image.MetaChannelCount;
        parameters.Clear();
        int w = image.Channels[image.MetaChannelCount].Width;
        int h = image.Channels[image.MetaChannelCount].Height;

        // Horizontal first on wide images; vertical first on tall ones.
        bool wide = w > h;
        if (numChannels > 2 && image.Channels[image.MetaChannelCount + 1].Width == w && image.Channels[image.MetaChannelCount + 1].Height == h)
        {
            // Channels 1 and 2 are presumably chroma and can be squeezed first for 4:2:0 previews.
            parameters.Add(new SqueezeParams(true, false, (uint)(image.MetaChannelCount + 1), 2));
            parameters.Add(new SqueezeParams(false, false, (uint)(image.MetaChannelCount + 1), 2));
        }

        uint begin = (uint)image.MetaChannelCount;
        const int maxFirstPreviewSize = 8;
        if (!wide && h > maxFirstPreviewSize)
        {
            parameters.Add(new SqueezeParams(false, true, begin, (uint)numChannels));
            h = (h + 1) / 2;
        }

        while (w > maxFirstPreviewSize || h > maxFirstPreviewSize)
        {
            if (w > maxFirstPreviewSize)
            {
                parameters.Add(new SqueezeParams(true, true, begin, (uint)numChannels));
                w = (w + 1) / 2;
            }

            if (h > maxFirstPreviewSize)
            {
                parameters.Add(new SqueezeParams(false, true, begin, (uint)numChannels));
                h = (h + 1) / 2;
            }
        }
    }

    private static void CheckSqueezeRange(SqueezeParams p, int numChannels)
    {
        long c1 = p.BeginChannel;
        long c2 = (long)p.BeginChannel + p.NumChannels - 1;
        if (c1 >= numChannels || c2 >= numChannels || c2 < c1)
        {
            throw new JxlDecodingException("Invalid squeeze channel range.");
        }
    }

    private static void MetaSqueeze(ModularImage image, List<SqueezeParams> parameters)
    {
        if (parameters.Count == 0)
        {
            DefaultSqueezeParameters(parameters, image);
        }

        foreach (var p in parameters)
        {
            CheckSqueezeRange(p, image.Channels.Count);
            int begin = (int)p.BeginChannel;
            int end = begin + (int)p.NumChannels - 1;
            if (begin < image.MetaChannelCount)
            {
                if (end >= image.MetaChannelCount)
                {
                    throw new JxlDecodingException("Invalid squeeze: mix of meta and non-meta channels.");
                }

                if (!p.InPlace)
                {
                    throw new JxlDecodingException("Invalid squeeze: meta channels require in-place residuals.");
                }

                image.MetaChannelCount += (int)p.NumChannels;
            }

            int offset = p.InPlace ? end + 1 : image.Channels.Count;
            for (int c = begin; c <= end; c++)
            {
                var channel = image.Channels[c];
                if (channel.HShift > 30 || channel.VShift > 30)
                {
                    throw new JxlDecodingException("Too many squeezes: shift > 30.");
                }

                int w = channel.Width;
                int h = channel.Height;
                if (w == 0 || h == 0)
                {
                    throw new JxlDecodingException("Squeezing an empty channel.");
                }

                if (p.Horizontal)
                {
                    int newW = (w + 1) / 2;
                    if (channel.HShift >= 0)
                    {
                        channel.HShift++;
                    }

                    channel.Resize(newW, h);
                    w -= newW;
                }
                else
                {
                    int newH = (h + 1) / 2;
                    if (channel.VShift >= 0)
                    {
                        channel.VShift++;
                    }

                    channel.Resize(w, newH);
                    h -= newH;
                }

                image.Channels.Insert(offset + (c - begin), new ModularChannel(w, h, channel.HShift, channel.VShift));
            }
        }
    }

    // Estimate of the difference C-D between the pair (C, D) averaging to `a`, given neighbours B and n, avoiding ringing.
    private static long SmoothTendency(long b, long a, long n)
    {
        long diff = 0;
        if (b >= a && a >= n)
        {
            diff = ((4 * b) - (3 * n) - a + 6) / 12;
            if (diff - (diff & 1) > 2 * (b - a))
            {
                diff = (2 * (b - a)) + 1;
            }

            if (diff + (diff & 1) > 2 * (a - n))
            {
                diff = 2 * (a - n);
            }
        }
        else if (b <= a && a <= n)
        {
            diff = ((4 * b) - (3 * n) - a - 6) / 12;
            if (diff + (diff & 1) < 2 * (b - a))
            {
                diff = (2 * (b - a)) - 1;
            }

            if (diff - (diff & 1) < 2 * (a - n))
            {
                diff = 2 * (a - n);
            }
        }

        return diff;
    }

    private static void InverseSqueeze(ModularImage image, List<SqueezeParams> parameters)
    {
        for (int i = parameters.Count - 1; i >= 0; i--)
        {
            var p = parameters[i];
            CheckSqueezeRange(p, image.Channels.Count);
            int begin = (int)p.BeginChannel;
            int end = begin + (int)p.NumChannels - 1;
            int offset = p.InPlace ? end + 1 : image.Channels.Count + begin - end - 1;
            if (begin < image.MetaChannelCount)
            {
                if (image.MetaChannelCount <= p.NumChannels)
                {
                    throw new JxlDecodingException("Corrupted squeeze transform.");
                }

                image.MetaChannelCount -= (int)p.NumChannels;
            }

            for (int c = begin; c <= end; c++)
            {
                int rc = offset + c - begin;
                if (rc >= image.Channels.Count)
                {
                    throw new JxlDecodingException("Corrupted squeeze transform.");
                }

                var input = image.Channels[c];
                var residual = image.Channels[rc];
                if (input.Width < residual.Width || input.Height < residual.Height)
                {
                    throw new JxlDecodingException("Corrupted squeeze transform.");
                }

                if (p.Horizontal)
                {
                    InverseHorizontalSqueeze(image, c, rc);
                }
                else
                {
                    InverseVerticalSqueeze(image, c, rc);
                }
            }

            for (int k = 0; k <= end - begin; k++)
            {
                image.Channels[offset].Dispose();
                image.Channels.RemoveAt(offset);
            }
        }
    }

    private static void InverseHorizontalSqueeze(ModularImage image, int c, int rc)
    {
        var input = image.Channels[c];
        var residual = image.Channels[rc];
        if (input.Width != (input.Width + residual.Width + 1) / 2 || input.Height != residual.Height)
        {
            throw new JxlDecodingException("Corrupted squeeze transform.");
        }

        if (residual.Width == 0)
        {
            input.HShift--;
            return;
        }

        var output = new ModularChannel(input.Width + residual.Width, input.Height, input.HShift - 1, input.VShift);
        if (residual.Height != 0)
        {
            for (int y = 0; y < input.Height; y++)
            {
                var pResidual = residual.ReadOnlyRow(y);
                var pAvg = input.ReadOnlyRow(y);
                var pOut = output.Row(y);
                for (int x = 0; x < residual.Width; x++)
                {
                    long diffMinusTendency = pResidual[x];
                    long avg = pAvg[x];
                    long nextAvg = x + 1 < input.Width ? pAvg[x + 1] : avg;
                    long left = x != 0 ? pOut[(x << 1) - 1] : avg;
                    long tendency = SmoothTendency(left, avg, nextAvg);
                    long diff = diffMinusTendency + tendency;
                    long a = avg + (diff / 2);
                    pOut[x << 1] = (int)a;
                    pOut[(x << 1) + 1] = (int)(a - diff);
                }

                if ((output.Width & 1) != 0)
                {
                    pOut[output.Width - 1] = pAvg[input.Width - 1];
                }
            }
        }

        input.Dispose();
        image.Channels[c] = output;
    }

    private static void InverseVerticalSqueeze(ModularImage image, int c, int rc)
    {
        var input = image.Channels[c];
        var residual = image.Channels[rc];
        if (input.Height != (input.Height + residual.Height + 1) / 2 || input.Width != residual.Width)
        {
            throw new JxlDecodingException("Corrupted squeeze transform.");
        }

        if (residual.Height == 0)
        {
            input.VShift--;
            return;
        }

        var output = new ModularChannel(input.Width, input.Height + residual.Height, input.HShift, input.VShift - 1);
        if (residual.Width != 0)
        {
            for (int y = 0; y < residual.Height; y++)
            {
                var pResidual = residual.ReadOnlyRow(y);
                var pAvg = input.ReadOnlyRow(y);
                var pNextAvg = input.ReadOnlyRow(y + 1 < input.Height ? y + 1 : y);
                var pOut = output.Row(y << 1);
                var pNextOut = output.Row((y << 1) + 1);
                ReadOnlySpan<int> pPrevOut = y > 0 ? output.ReadOnlyRow((y << 1) - 1) : pAvg;
                for (int x = 0; x < input.Width; x++)
                {
                    long avg = pAvg[x];
                    long nextAvg = pNextAvg[x];
                    long top = pPrevOut[x];
                    long tendency = SmoothTendency(top, avg, nextAvg);
                    long diff = pResidual[x] + tendency;
                    long result = avg + (diff / 2);
                    pOut[x] = (int)result;
                    pNextOut[x] = (int)(result - diff);
                }
            }

            if ((output.Height & 1) != 0)
            {
                input.ReadOnlyRow(input.Height - 1).CopyTo(output.Row((input.Height - 1) << 1));
            }
        }

        input.Dispose();
        image.Channels[c] = output;
    }
}
