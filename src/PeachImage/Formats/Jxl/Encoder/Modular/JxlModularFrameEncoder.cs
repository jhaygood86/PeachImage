using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Entropy;
using PeachImage.Formats.Jxl.Encoder.Headers;
using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Modular;

namespace PeachImage.Formats.Jxl.Encoder.Modular;

/// <summary>The integer channels of one frame: colour first (1 or 3 of them), then extra channels such as alpha, all at full size.</summary>
internal sealed class JxlModularFrameInput
{
    public JxlModularFrameInput(int width, int height, int colorChannels, int[][] channels, bool colorTransform = true)
    {
        ColorTransform = colorTransform;
        Width = width;
        Height = height;
        ColorChannels = colorChannels;
        Channels = channels;
    }

    public int Width { get; }

    public int Height { get; }

    public int ColorChannels { get; }

    /// <summary>Whether RGB planes go through the reversible YCoCg transform (not for float bit patterns, which it would only scramble).</summary>
    public bool ColorTransform { get; }

    /// <summary>One <c>Width * Height</c> plane per channel. The encoder transforms these in place.</summary>
    public int[][] Channels { get; }
}

/// <summary>
/// Encodes a lossless Modular frame: a reversible colour transform, an MA tree with one gradient-predicted leaf per
/// channel, and rANS-coded residuals, split into 256x256 groups that are tokenized and written in parallel.
/// </summary>
internal static class JxlModularFrameEncoder
{
    private const int GroupDimension = 128 << JxlFrameHeaderWriter.GroupSizeShift;

    /// <summary>Encodes <paramref name="input"/> as a complete frame (header, table of contents and sections).</summary>
    public static byte[] Encode(JxlModularFrameInput input, bool xybEncoded)
    {
        int width = input.Width;
        int height = input.Height;
        var channels = input.Channels;
        int numExtra = channels.Length - input.ColorChannels;

        var transforms = new List<(JxlModularTransformKind Kind, int Begin, int Type)>();
        if (input.ColorChannels == 3 && input.ColorTransform)
        {
            JxlForwardRct.YCoCg(channels[0], channels[1], channels[2]);
            transforms.Add((JxlModularTransformKind.Rct, 0, 6));
        }

        var predictors = new ModularPredictor[channels.Length];
        Array.Fill(predictors, ModularPredictor.Gradient);
        var tree = channels.Length == 1
            ? new JxlMaTreeEncoder(JxlMaTreeNode.Leaf(predictors[0]))
            : JxlMaTreeEncoder.PerChannel(predictors);

        var dims = new JxlFrameDimensions(width, height, JxlFrameHeaderWriter.GroupSizeShift, 0, 0, modular: true, upsampling: 1);
        bool singleSection = dims.NumGroups == 1;

        // Channels no larger than a group are coded in the global stream, larger ones in per-group streams.
        var globalTokens = new JxlTokenList();
        var groupTokens = new JxlTokenList[singleSection ? 0 : dims.NumGroups];
        if (singleSection)
        {
            for (int c = 0; c < channels.Length; c++)
            {
                TokenizeChannel(channels[c], width, height, tree.LeafForChannel(c), globalTokens);
            }
        }
        else
        {
            JxlParallel.For(dims.NumGroups, g =>
            {
                var tokens = new JxlTokenList();
                int x0 = g % dims.XSizeGroups * GroupDimension;
                int y0 = g / dims.XSizeGroups * GroupDimension;
                int w = Math.Min(GroupDimension, width - x0);
                int h = Math.Min(GroupDimension, height - y0);
                int[] rect = new int[w * h];
                for (int c = 0; c < channels.Length; c++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        channels[c].AsSpan(((y0 + y) * width) + x0, w).CopyTo(rect.AsSpan(y * w, w));
                    }

                    TokenizeChannel(rect, w, h, tree.LeafForChannel(c), tokens);
                }

                groupTokens[g] = tokens;
            });
        }

        // Entropy codes: one for the tree itself, one shared by every pixel stream of the frame.
        var treeTokens = new JxlTokenList();
        tree.WriteTokens(treeTokens);
        var treePlan = JxlEntropyPlan.Create(JxlMaTreeEncoder.NumTreeContexts, treeTokens);
        var pixelStreams = new List<JxlTokenList>(groupTokens.Length + 1) { globalTokens };
        pixelStreams.AddRange(groupTokens);
        var pixelPlan = JxlEntropyPlan.Create(tree.LeafCount, pixelStreams);

        // Section 0: the global section.
        var global = new JxlBitWriter();
        global.WriteBool(true); // default DC quantization
        global.WriteBool(true); // has a global MA tree
        treePlan.WriteHeader(global);
        treePlan.WriteStream(global, treeTokens);
        pixelPlan.WriteHeader(global);
        WriteStreamHeader(global, transforms);
        if (globalTokens.Count > 0)
        {
            pixelPlan.WriteStream(global, globalTokens);
        }

        byte[][] sections;
        if (singleSection)
        {
            sections = [global.ToArray()];
        }
        else
        {
            int numDc = dims.NumDcGroups;
            sections = new byte[2 + numDc + dims.NumGroups][];
            sections[0] = global.ToArray();
            for (int i = 1; i < 2 + numDc; i++)
            {
                sections[i] = [];
            }

            int groupBase = 2 + numDc;
            JxlParallel.For(dims.NumGroups, g =>
            {
                var writer = new JxlBitWriter(Math.Max(256, groupTokens[g].Count / 2));
                WriteStreamHeader(writer, []);
                pixelPlan.WriteStream(writer, groupTokens[g]);
                sections[groupBase + g] = writer.ToArray();
            });
        }

        var frame = new JxlBitWriter();
        JxlFrameHeaderWriter.Write(frame, new JxlFrameHeaderSpec(Modular: true, xybEncoded, numExtra));
        WriteToc(frame, sections);
        int total = 0;
        foreach (byte[] section in sections)
        {
            total += section.Length;
        }

        byte[] header = frame.ToArray();
        byte[] result = new byte[header.Length + total];
        header.CopyTo(result, 0);
        int position = header.Length;
        foreach (byte[] section in sections)
        {
            section.CopyTo(result, position);
            position += section.Length;
        }

        return result;
    }

    /// <summary>Writes the table of contents: no permutation, then each section's byte size, byte aligned.</summary>
    internal static void WriteToc(JxlBitWriter writer, IReadOnlyList<byte[]> sections)
    {
        writer.WriteBool(false); // no permutation
        writer.ZeroPadToByte();
        foreach (byte[] section in sections)
        {
            writer.WriteU32((uint)section.Length, U32Dist.Bits(10), U32Dist.BitsOffset(14, 1024), U32Dist.BitsOffset(22, 17408), U32Dist.BitsOffset(30, 4211712));
        }

        writer.ZeroPadToByte();
    }

    // The header of one Modular stream: the frame's tree is used, default weighted-predictor parameters, then the transforms.
    internal static void WriteStreamHeader(JxlBitWriter writer, List<(JxlModularTransformKind Kind, int Begin, int Type)> transforms)
    {
        writer.WriteBool(true); // use_global_tree
        writer.WriteBool(true); // default weighted predictor header
        writer.WriteU32((uint)transforms.Count, U32Dist.Val(0), U32Dist.Val(1), U32Dist.BitsOffset(4, 2), U32Dist.BitsOffset(8, 18));
        foreach (var (kind, begin, type) in transforms)
        {
            writer.WriteU32((uint)kind, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3));
            writer.WriteU32((uint)begin, U32Dist.Bits(3), U32Dist.BitsOffset(6, 8), U32Dist.BitsOffset(10, 72), U32Dist.BitsOffset(13, 1096));
            if (kind == JxlModularTransformKind.Rct)
            {
                writer.WriteU32((uint)type, U32Dist.Val(6), U32Dist.Bits(2), U32Dist.BitsOffset(4, 2), U32Dist.BitsOffset(6, 10));
            }
        }
    }

    internal static void TokenizeChannel(ReadOnlySpan<int> data, int width, int height, JxlMaTreeNode leaf, JxlTokenList tokens)
    {
        if (leaf.Predictor == ModularPredictor.Weighted)
        {
            throw new NotSupportedException("The weighted predictor is not implemented in the encoder yet.");
        }

        int context = leaf.LeafId;
        var predictor = leaf.Predictor;
        long offset = leaf.Offset;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var neighbors = new ModularNeighbors(data, width, x, y);
                long guess = offset + ModularPredictors.Predict(predictor, neighbors, 0);
                // Wraps like the decoder's 32-bit arithmetic, which float bit patterns rely on.
                tokens.Add(context, JxlMaTreeEncoder.PackSigned(unchecked((int)(data[(y * width) + x] - guess))));
            }
        }
    }
}

/// <summary>The Modular transforms an encoder can apply (the values written in a stream header).</summary>
internal enum JxlModularTransformKind : uint
{
    Rct = 0,
    Palette = 1,
    Squeeze = 2,
}

/// <summary>The forward counterparts of the decoder's inverse reversible colour transforms.</summary>
internal static class JxlForwardRct
{
    /// <summary>Converts RGB planes in place to (Y, Co, Cg), the exactly invertible YCoCg-R transform (RCT type 6).</summary>
    public static void YCoCg(int[] r, int[] g, int[] b)
    {
        for (int i = 0; i < r.Length; i++)
        {
            int co = r[i] - b[i];
            int tmp = b[i] + (co >> 1);
            int cg = g[i] - tmp;
            int y = tmp + (cg >> 1);
            r[i] = y;
            g[i] = co;
            b[i] = cg;
        }
    }
}
