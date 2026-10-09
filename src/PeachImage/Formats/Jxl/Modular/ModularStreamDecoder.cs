using System.Buffers;
using System.Runtime.CompilerServices;
using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Entropy;

namespace PeachImage.Formats.Jxl.Modular;

/// <summary>The header of one Modular stream: whether the frame's global tree is used, predictor tuning, and the transforms.</summary>
internal sealed class ModularGroupHeader
{
    public ModularGroupHeader()
    {
    }

    public bool UseGlobalTree { get; private init; }

    public WeightedPredictorHeader WeightedHeader { get; private init; } = WeightedPredictorHeader.Default;

    public List<ModularTransform> Transforms { get; private init; } = [];

    public static ModularGroupHeader Read(ref JxlBitReader br)
    {
        bool useGlobalTree = br.ReadBool();
        var wp = WeightedPredictorHeader.Read(ref br);
        uint count = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.BitsOffset(4, 2), U32Dist.BitsOffset(8, 18));
        br.ThrowIfOverrun();
        if (count > 512)
        {
            throw new JxlDecodingException("Too many Modular transforms.");
        }

        var transforms = new List<ModularTransform>((int)count);
        for (int i = 0; i < count; i++)
        {
            transforms.Add(ModularTransform.Read(ref br));
        }

        return new ModularGroupHeader { UseGlobalTree = useGlobalTree, WeightedHeader = wp, Transforms = transforms };
    }
}

/// <summary>A frame-wide Modular context model: the MA tree plus the entropy code its leaves' contexts index into.</summary>
internal sealed class ModularContextModel
{
    public ModularContextModel(MaTree tree, JxlEntropyCode code, byte[] contextMap)
    {
        Tree = tree;
        Code = code;
        ContextMap = contextMap;
    }

    public MaTree Tree { get; }

    public JxlEntropyCode Code { get; }

    public byte[] ContextMap { get; }

    /// <summary>Reads a tree and its histograms from the bitstream.</summary>
    public static ModularContextModel Read(ref JxlBitReader br, int treeSizeLimit)
    {
        var tree = MaTree.Read(ref br, treeSizeLimit);
        var code = JxlEntropyCode.Read(ref br, (tree.Count + 1) / 2, out byte[] contextMap);
        return new ModularContextModel(tree, code, contextMap);
    }
}

/// <summary>Decodes the pixel data of a Modular stream (a global stream or a group stream).</summary>
internal static class ModularStreamDecoder
{
    /// <summary>
    /// Decodes <paramref name="image"/> from <paramref name="br"/>: reads the stream header, applies the transforms'
    /// channel-layout changes, then decodes the channels up to the first non-meta channel larger than
    /// <paramref name="maxChannelSize"/>. Transforms are left applied unless <paramref name="undoTransforms"/>.
    /// </summary>
    public static ModularGroupHeader Decode(
        ref JxlBitReader br,
        ModularImage image,
        uint streamId,
        int maxChannelSize,
        int groupDim,
        ModularContextModel? globalModel,
        bool undoTransforms)
    {
        // A stream with no channels (for example a VarDCT frame's global stream without extra channels) carries nothing at all.
        if (image.Channels.Count == 0)
        {
            return new ModularGroupHeader();
        }

        var header = ModularGroupHeader.Read(ref br);

        image.Transforms = header.Transforms;
        foreach (var transform in image.Transforms)
        {
            transform.MetaApply(image);
        }

        ValidateChannelDimensions(image, groupDim);

        int numChannels = 0;
        int distanceMultiplier = 0;
        foreach (var (channel, index) in image.Channels.Select((c, i) => (c, i)))
        {
            if (index >= image.MetaChannelCount && (channel.Width > maxChannelSize || channel.Height > maxChannelSize))
            {
                break;
            }

            if (channel.Width == 0 || channel.Height == 0)
            {
                continue;
            }

            distanceMultiplier = Math.Max(distanceMultiplier, channel.Width);
            numChannels++;
        }

        if (numChannels != 0)
        {
            ModularContextModel model;
            if (!header.UseGlobalTree)
            {
                long maxTreeSize = 1024;
                for (int i = 0; i < image.Channels.Count; i++)
                {
                    var channel = image.Channels[i];
                    if (i >= image.MetaChannelCount && (channel.Width > maxChannelSize || channel.Height > maxChannelSize))
                    {
                        break;
                    }

                    maxTreeSize += (long)channel.Width * channel.Height;
                }

                model = ModularContextModel.Read(ref br, (int)Math.Min(1 << 20, maxTreeSize));
            }
            else
            {
                model = globalModel is { Tree.Count: > 0 }
                    ? globalModel
                    : throw new JxlDecodingException("A global MA tree was requested but none is available.");
            }

            using var reader = new JxlSymbolReader(model.Code, ref br, distanceMultiplier);
            for (int i = 0; i < image.Channels.Count; i++)
            {
                var channel = image.Channels[i];
                if (i >= image.MetaChannelCount && (channel.Width > maxChannelSize || channel.Height > maxChannelSize))
                {
                    break;
                }

                if (channel.Width == 0 || channel.Height == 0)
                {
                    continue;
                }

                DecodeChannel(ref br, reader, model, header.WeightedHeader, image, i, streamId);
            }

            br.ThrowIfOverrun();
            if (!reader.CheckFinalState())
            {
                throw new JxlDecodingException("ANS final state check failed while decoding a Modular stream.");
            }
        }

        if (undoTransforms)
        {
            image.UndoTransforms(header.WeightedHeader);
        }

        return header;
    }

    private static void ValidateChannelDimensions(ModularImage image, int groupDim)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool isDc = pass == 0;
            int dim = groupDim * (isDc ? JxlFrameDimensionsConstants.BlockDim : 1);
            int c = image.MetaChannelCount;
            for (; c < image.Channels.Count; c++)
            {
                var channel = image.Channels[c];
                if (channel.Width > groupDim || channel.Height > groupDim)
                {
                    break;
                }
            }

            for (; c < image.Channels.Count; c++)
            {
                var channel = image.Channels[c];
                if (channel.Width == 0 || channel.Height == 0)
                {
                    continue;
                }

                bool isDcChannel = Math.Min(channel.HShift, channel.VShift) >= 3;
                if (isDcChannel != isDc)
                {
                    continue;
                }

                if ((dim >> Math.Max(channel.HShift, channel.VShift)) == 0)
                {
                    throw new JxlDecodingException("Inconsistent Modular transforms.");
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DecodeChannel(
        ref JxlBitReader br,
        JxlSymbolReader reader,
        ModularContextModel model,
        WeightedPredictorHeader wpHeader,
        ModularImage image,
        int channelIndex,
        uint streamId)
    {
        var channel = image.Channels[channelIndex];
        int width = channel.Width;
        int height = channel.Height;
        var tree = model.Tree;
        var contextMap = model.ContextMap;
        var data = channel.Samples;

        int numProperties = tree.NumProperties;
        int numExtra = numProperties - MaTree.NumNonRefProperties;
        int[] props = ArrayPool<int>.Shared.Rent(numProperties);
        props.AsSpan(0, numProperties).Clear();

        // Earlier channels with the same geometry contribute four properties each, nearest first.
        var references = new List<ModularChannel>();
        for (int j = channelIndex - 1; j >= 0 && references.Count * MaTree.ExtraPropertiesPerChannel < numExtra; j--)
        {
            if (image.Channels[j].SameGeometry(channel))
            {
                references.Add(image.Channels[j]);
            }
        }

        var wp = tree.UsesWeightedPredictor ? new WeightedPredictorState(wpHeader, width) : null;

        int[] treeProperty = tree.Property;
        int[] treeSplit = tree.SplitValue;
        int[] treeLeft = tree.Left;
        int[] treeRight = tree.Right;
        int chan = channelIndex;
        int group = (int)streamId;

        try
        {
            for (int y = 0; y < height; y++)
            {
                props[0] = chan;
                props[1] = group;
                props[2] = y;
                props[9] = 0;

                for (int x = 0; x < width; x++)
                {
                    int pos = (y * width) + x;
                    var n = new ModularNeighbors(data, width, x, y);

                    props[3] = x;
                    props[4] = (int)(n.Top > 0 ? n.Top : -n.Top);
                    props[5] = (int)(n.Left > 0 ? n.Left : -n.Left);
                    props[6] = (int)n.Top;
                    props[7] = (int)n.Left;
                    props[8] = (int)(n.Left - props[9]);
                    props[9] = (int)(n.Left + n.Top - n.TopLeft);
                    props[10] = (int)(n.Left - n.TopLeft);
                    props[11] = (int)(n.TopLeft - n.Top);
                    props[12] = (int)(n.Top - n.TopRight);
                    props[13] = (int)(n.Top - n.TopTop);
                    props[14] = (int)(n.Left - n.LeftLeft);

                    long weightedPrediction = 0;
                    if (wp is not null)
                    {
                        weightedPrediction = wp.Predict(x, y, width, n.Top, n.Left, n.TopRight, n.TopLeft, n.TopTop, out int wpProperty);
                        props[MaTree.WeightedProperty] = wpProperty;
                    }

                    for (int k = 0; k < references.Count; k++)
                    {
                        FillReferenceProperties(references[k], x, y, props.AsSpan(MaTree.NumNonRefProperties + (k * MaTree.ExtraPropertiesPerChannel), 4));
                    }

                    // Walk the tree: values greater than the split go left.
                    int node = 0;
                    while (treeProperty[node] >= 0)
                    {
                        int property = treeProperty[node];
                        int value = property switch
                        {
                            0 => chan,
                            1 => group,
                            _ => props[property],
                        };
                        node = value > treeSplit[node] ? treeLeft[node] : treeRight[node];
                    }

                    int context = contextMap[treeLeft[node]];
                    long guess = tree.Offset[node] + ModularPredictors.Predict(tree.Predictor[node], n, weightedPrediction);
                    uint token = reader.ReadHybridUint(context, ref br);
                    long residual = JxlFieldReader.UnpackSigned(token);
                    int sample = unchecked((int)((residual * tree.Multiplier[node]) + guess));
                    data[pos] = sample;
                    wp?.UpdateErrors(sample, x, y, width);
                }

                br.ThrowIfOverrun();
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(props);
        }
    }

    // The four properties a reference channel adds for a pixel: |v|, v, |v - gradient|, v - gradient.
    private static void FillReferenceProperties(ModularChannel reference, int x, int y, Span<int> destination)
    {
        var current = reference.ReadOnlyRow(y);
        long v = current[x];
        long left = x != 0 ? current[x - 1] : 0;
        long top = y != 0 ? reference.ReadOnlyRow(y - 1)[x] : left;
        long topLeft = x != 0 && y != 0 ? reference.ReadOnlyRow(y - 1)[x - 1] : left;
        long predicted = ModularPredictors.ClampedGradient((int)left, (int)top, (int)topLeft);
        destination[0] = (int)Math.Abs(v);
        destination[1] = (int)v;
        destination[2] = (int)Math.Abs(v - predicted);
        destination[3] = (int)(v - predicted);
    }
}

/// <summary>Constants shared with the frame layer.</summary>
internal static class JxlFrameDimensionsConstants
{
    public const int BlockDim = 8;
}
