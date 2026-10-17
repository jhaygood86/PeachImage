using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Entropy;
using PeachImage.Formats.Jxl.Encoder.Headers;
using PeachImage.Formats.Jxl.Encoder.Modular;
using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Jxl.Modular;
using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Formats.Jxl.Encoder.VarDct;

/// <summary>The sample planes of a VarDCT frame: sRGB-encoded colour (one gray plane or three RGB planes), then extra channels such as alpha.</summary>
internal sealed class JxlVarDctFrameInput
{
    public JxlVarDctFrameInput(int width, int height, int bitsPerSample, int colorChannels, int[][] channels, bool isFloat = false)
    {
        IsFloat = isFloat;
        Width = width;
        Height = height;
        BitsPerSample = bitsPerSample;
        ColorChannels = colorChannels;
        Channels = channels;
    }

    public int Width { get; }

    public int Height { get; }

    public int BitsPerSample { get; }

    public int ColorChannels { get; }

    /// <summary>Whether the samples are 32-bit floats stored as their bit patterns (the colour planes are then read back as sRGB-encoded floats).</summary>
    public bool IsFloat { get; }

    /// <summary>Colour planes first, then extra channels; every plane holds <c>Width * Height</c> samples.</summary>
    public int[][] Channels { get; }
}

/// <summary>
/// Encodes a lossy VarDCT frame: the image goes to XYB, every 8x8 block is DCT-transformed and quantized with a single
/// frame-wide step, the quantized DC is coded as a small Modular image per DC group and the AC coefficients are
/// context-modelled and rANS-coded per 256x256 group.
/// </summary>
internal static class JxlVarDctFrameEncoder
{
    // The raw quantization value every block carries; the global scale is chosen so base * RawQuant gives the requested step.
    private const int RawQuant = 64;
    private const int GroupBlocks = 32;
    private const int BlockSize = 64;
    private const int NumQuantTables = 17;
    private const int NumMetaChannels = 4;

    public static byte[] Encode(JxlVarDctFrameInput input, JxlVarDctSettings settings)
    {
        int width = input.Width;
        int height = input.Height;
        var dims = new JxlFrameDimensions(width, height, JxlFrameHeaderWriter.GroupSizeShift, 0, 0, modular: false, upsampling: 1);
        int blocksX = dims.XSizeBlocks;
        int blocksY = dims.YSizeBlocks;
        int numExtra = input.Channels.Length - input.ColorChannels;
        bool singleSection = dims.NumGroups == 1;

        // Quantizer: the global scale realises the requested AC step at the base per-block raw quantization.
        int rawBase = JxlVarDctFrameState.BaseRawQuant;
        int globalScale = Math.Clamp((int)MathF.Round(65536f / (settings.AcScale * rawBase)), 1, 8193 + 65535);
        float invGlobalScale = 65536f / globalScale;
        float inverseQuantDc = InverseQuantDc(globalScale, settings.QuantDc);
        var matrices = new DequantMatrices();
        float[] dcMultipliers = [inverseQuantDc * matrices.DcQuant[0], inverseQuantDc * matrices.DcQuant[1], inverseQuantDc * matrices.DcQuant[2]];

        float[][] planes = JxlForwardXyb.Convert(input.Channels, input.ColorChannels, width, height, input.BitsPerSample, input.IsFloat, blocksX * 8, blocksY * 8);
        if (settings.Gaborish)
        {
            JxlGaborish.Sharpen(planes, blocksX * 8, width, height, blocksY * 8);
        }

        var state = new JxlVarDctFrameState(dims, planes, settings, matrices, invGlobalScale, dcMultipliers);
        JxlVarDctAnalysis.Run(state);

        // The tree: the quantized-DC and metadata streams share leaves by channel; extra channels get their own.
        var tree = BuildTree(dims, numExtra > 0);

        var acTokens = new JxlTokenList[dims.NumGroups];
        JxlParallel.For(dims.NumGroups, g => acTokens[g] = JxlVarDctGroupEncoder.Encode(state, g));

        // DC group and AC-metadata token streams.
        var dcTokens = new JxlTokenList[dims.NumDcGroups];
        var metaTokens = new JxlTokenList[dims.NumDcGroups];
        var metaCounts = new int[dims.NumDcGroups];
        for (int g = 0; g < dims.NumDcGroups; g++)
        {
            (int x0, int y0, int w, int h) = DcGroupRect(dims, g);
            dcTokens[g] = new JxlTokenList();
            int dcStream = 1 + g;
            int[] qy = ExtractRect(state.QuantizedDc[1], blocksX, x0, y0, w, h);
            int[] qx = ExtractRect(state.QuantizedDc[0], blocksX, x0, y0, w, h);
            int[] qb = ExtractRect(state.QuantizedDc[2], blocksX, x0, y0, w, h);
            JxlModularFrameEncoder.TokenizeChannel(qy, w, h, tree.LeafFor(dcStream, 0), dcTokens[g]);
            JxlModularFrameEncoder.TokenizeChannel(qx, w, h, tree.LeafFor(dcStream, 1), dcTokens[g]);
            JxlModularFrameEncoder.TokenizeChannel(qb, w, h, tree.LeafFor(dcStream, 2), dcTokens[g]);

            metaTokens[g] = new JxlTokenList();
            int metaStream = 1 + (2 * dims.NumDcGroups) + g;
            int tileW = (w + 7) >> 3;
            int tileH = (h + 7) >> 3;
            int tileX0 = x0 >> 3;
            int tileY0 = y0 >> 3;
            int[] ytox = new int[tileW * tileH];
            int[] ytob = new int[tileW * tileH];
            for (int ty = 0; ty < tileH; ty++)
            {
                for (int tx = 0; tx < tileW; tx++)
                {
                    ytox[(ty * tileW) + tx] = state.YToX[((tileY0 + ty) * state.TilesX) + tileX0 + tx];
                    ytob[(ty * tileW) + tx] = state.YToB[((tileY0 + ty) * state.TilesX) + tileX0 + tx];
                }
            }

            // The first cell of every transform, in the raster order the decoder walks them: its type and raw quantization value.
            var firsts = new List<int>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int cell = ((y0 + y) * blocksX) + x0 + x;
                    if (state.CellStrategy[cell] != JxlVarDctFrameState.Covered)
                    {
                        firsts.Add(cell);
                    }
                }
            }

            int count = firsts.Count;
            int[] strategyAndQuant = new int[count * 2];
            for (int i = 0; i < count; i++)
            {
                strategyAndQuant[i] = state.CellStrategy[firsts[i]];
                strategyAndQuant[count + i] = state.RawQuant[firsts[i]] - 1;
            }

            JxlModularFrameEncoder.TokenizeChannel(ytox, tileW, tileH, tree.LeafFor(metaStream, 0), metaTokens[g]);
            JxlModularFrameEncoder.TokenizeChannel(ytob, tileW, tileH, tree.LeafFor(metaStream, 1), metaTokens[g]);
            JxlModularFrameEncoder.TokenizeChannel(strategyAndQuant, count, 2, tree.LeafFor(metaStream, 2), metaTokens[g]);
            int[] sharpness = new int[w * h];
            Array.Fill(sharpness, settings.EpfSharpness);
            JxlModularFrameEncoder.TokenizeChannel(sharpness, w, h, tree.LeafFor(metaStream, 3), metaTokens[g]);
            metaCounts[g] = count;
        }

        // Extra channels (alpha): in the global stream when they fit a group, otherwise in each AC group's section.
        var globalTokens = new JxlTokenList();
        var extraGroupTokens = new JxlTokenList[singleSection || numExtra == 0 ? 0 : dims.NumGroups];
        int[][] extras = input.Channels[input.ColorChannels..];
        if (numExtra > 0)
        {
            if (singleSection)
            {
                for (int e = 0; e < numExtra; e++)
                {
                    JxlModularFrameEncoder.TokenizeChannel(extras[e], width, height, tree.LeafFor(0, e), globalTokens);
                }
            }
            else
            {
                JxlParallel.For(dims.NumGroups, g =>
                {
                    var tokens = new JxlTokenList();
                    int x0 = g % dims.XSizeGroups * dims.GroupDimension;
                    int y0 = g / dims.XSizeGroups * dims.GroupDimension;
                    int w = Math.Min(dims.GroupDimension, width - x0);
                    int h = Math.Min(dims.GroupDimension, height - y0);
                    int stream = 1 + (3 * dims.NumDcGroups) + NumQuantTables + g;
                    for (int e = 0; e < numExtra; e++)
                    {
                        TokenizeChannel(ExtractRect(extras[e], width, x0, y0, w, h), w, h, tree.LeafFor(stream, e), tokens);
                    }

                    extraGroupTokens[g] = tokens;
                });
            }
        }

        // Entropy codes.
        var treeTokens = new JxlTokenList();
        tree.WriteTokens(treeTokens);
        var treePlan = JxlEntropyPlan.Create(JxlMaTreeEncoder.NumTreeContexts, treeTokens);
        var pixelStreams = new List<JxlTokenList>(dcTokens.Length + metaTokens.Length + extraGroupTokens.Length + 1) { globalTokens };
        pixelStreams.AddRange(dcTokens);
        pixelStreams.AddRange(metaTokens);
        pixelStreams.AddRange(extraGroupTokens);
        var pixelPlan = JxlEntropyPlan.Create(tree.LeafCount, pixelStreams);
        var acPlan = JxlEntropyPlan.Create(state.BlockContext.NumAcContexts, acTokens);

        // Global section.
        var global = new JxlBitWriter();
        global.WriteBool(true); // default DC dequantization multipliers
        WriteQuantizer(global, globalScale, settings.QuantDc);
        global.WriteBool(true); // default block context map
        WriteCorrelation(global, state);
        global.WriteBool(true); // has a global MA tree
        treePlan.WriteHeader(global);
        treePlan.WriteStream(global, treeTokens);
        pixelPlan.WriteHeader(global);
        if (numExtra > 0)
        {
            JxlModularFrameEncoder.WriteStreamHeader(global, []);
            if (globalTokens.Count > 0)
            {
                pixelPlan.WriteStream(global, globalTokens);
            }
        }

        var dcSections = new JxlBitWriter[dims.NumDcGroups];
        for (int g = 0; g < dims.NumDcGroups; g++)
        {
            var writer = singleSection ? global : new JxlBitWriter();
            (_, _, int w, int h) = DcGroupRect(dims, g);
            writer.WriteBits(0, 2); // extra precision
            JxlModularFrameEncoder.WriteStreamHeader(writer, []);
            pixelPlan.WriteStream(writer, dcTokens[g]);
            int upperBound = w * h;
            int bits = upperBound <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(upperBound - 1));
            writer.WriteBits((uint)(metaCounts[g] - 1), bits);
            JxlModularFrameEncoder.WriteStreamHeader(writer, []);
            pixelPlan.WriteStream(writer, metaTokens[g]);
            dcSections[g] = writer;
        }

        var acGlobal = singleSection ? global : new JxlBitWriter();
        acGlobal.WriteBool(true); // default dequantization matrices
        int groupBits = dims.NumGroups <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(dims.NumGroups - 1));
        acGlobal.WriteBits(0, groupBits); // one histogram set
        acGlobal.WriteU32(0, U32Dist.Val(0x5F), U32Dist.Val(0x13), U32Dist.Val(0), U32Dist.Bits(13)); // natural coefficient orders
        acPlan.WriteHeader(acGlobal);

        byte[][] sections;
        if (singleSection)
        {
            acPlan.WriteStream(global, acTokens[0]);
            sections = [global.ToArray()];
        }
        else
        {
            sections = new byte[2 + dims.NumDcGroups + dims.NumGroups][];
            sections[0] = global.ToArray();
            for (int g = 0; g < dims.NumDcGroups; g++)
            {
                sections[1 + g] = dcSections[g].ToArray();
            }

            sections[1 + dims.NumDcGroups] = acGlobal.ToArray();
            int groupBase = 2 + dims.NumDcGroups;
            JxlParallel.For(dims.NumGroups, g =>
            {
                var writer = new JxlBitWriter(Math.Max(256, acTokens[g].Count));
                acPlan.WriteStream(writer, acTokens[g]);
                if (numExtra > 0)
                {
                    JxlModularFrameEncoder.WriteStreamHeader(writer, []);
                    pixelPlan.WriteStream(writer, extraGroupTokens[g]);
                }

                sections[groupBase + g] = writer.ToArray();
            });
        }

        var frame = new JxlBitWriter();
        JxlFrameHeaderWriter.Write(frame, new JxlFrameHeaderSpec(Modular: false, XybEncoded: true, numExtra, settings.XQmScale, settings.BQmScale, settings.Gaborish, settings.EpfIterations));
        JxlModularFrameEncoder.WriteToc(frame, sections);
        byte[] header = frame.ToArray();
        int total = header.Length;
        foreach (byte[] section in sections)
        {
            total += section.Length;
        }

        byte[] result = new byte[total];
        header.CopyTo(result, 0);
        int position = header.Length;
        foreach (byte[] section in sections)
        {
            section.CopyTo(result, position);
            position += section.Length;
        }

        return result;
    }

    private static void TokenizeChannel(ReadOnlySpan<int> data, int width, int height, JxlMaTreeNode leaf, JxlTokenList tokens) =>
        JxlModularFrameEncoder.TokenizeChannel(data, width, height, leaf, tokens);

    // The frame's chroma-from-luma parameters: the defaults, unless DC wants its own correlation.
    private static void WriteCorrelation(JxlBitWriter writer, JxlVarDctFrameState state)
    {
        if (state.DcFactorX == 0 && state.DcFactorB == 0)
        {
            writer.WriteBool(true);
            return;
        }

        writer.WriteBool(false);
        writer.WriteU32(JxlVarDctConstants.ColorFactor, U32Dist.Val(JxlVarDctConstants.ColorFactor), U32Dist.Val(256), U32Dist.BitsOffset(8, 2), U32Dist.BitsOffset(16, 258));
        writer.WriteF16(0f);  // base correlation of X
        writer.WriteF16(1f);  // base correlation of B
        writer.WriteBits((uint)(state.DcFactorX + 128), 8);
        writer.WriteBits((uint)(state.DcFactorB + 128), 8);
    }

    private static float InverseQuantDc(int globalScale, int quantDc) => 65536f / globalScale / quantDc;

    private static void WriteQuantizer(JxlBitWriter writer, int globalScale, int quantDc)
    {
        writer.WriteU32((uint)globalScale, U32Dist.BitsOffset(11, 1), U32Dist.BitsOffset(11, 2049), U32Dist.BitsOffset(12, 4097), U32Dist.BitsOffset(16, 8193));
        writer.WriteU32((uint)quantDc, U32Dist.Val(16), U32Dist.BitsOffset(5, 1), U32Dist.BitsOffset(8, 1), U32Dist.BitsOffset(16, 1));
    }

    // Streams: 0 global; 1.. DC groups; then AC-metadata groups; then everything from 1 + 3 * DC groups + 17 on is an AC group's extra channels.
    private static JxlMaTreeEncoder BuildTree(JxlFrameDimensions dims, bool hasExtra)
    {
        if (!hasExtra)
        {
            return JxlMaTreeEncoder.PerChannel([ModularPredictor.Gradient, ModularPredictor.Gradient, ModularPredictor.Gradient, ModularPredictor.Gradient]);
        }

        JxlMaTreeNode Channels(int lo, int hi)
        {
            if (lo == hi)
            {
                return JxlMaTreeNode.Leaf(ModularPredictor.Gradient);
            }

            int mid = (lo + hi) / 2;
            return JxlMaTreeNode.Inner(0, mid, Channels(mid + 1, hi), Channels(lo, mid));
        }

        var extraAc = JxlMaTreeNode.Leaf(ModularPredictor.Gradient);
        var extraGlobal = JxlMaTreeNode.Leaf(ModularPredictor.Gradient);
        var groupSide = JxlMaTreeNode.Inner(1, (3 * dims.NumDcGroups) + NumQuantTables, extraAc, Channels(0, NumMetaChannels - 1));
        return new JxlMaTreeEncoder(JxlMaTreeNode.Inner(1, 0, groupSide, extraGlobal));
    }

    internal static (int X0, int Y0, int Width, int Height) DcGroupRect(JxlFrameDimensions dims, int group)
    {
        int gx = group % dims.XSizeDcGroups;
        int gy = group / dims.XSizeDcGroups;
        int x0 = gx * dims.GroupDimension;
        int y0 = gy * dims.GroupDimension;
        return (x0, y0, Math.Min(dims.GroupDimension, dims.XSizeBlocks - x0), Math.Min(dims.GroupDimension, dims.YSizeBlocks - y0));
    }

    internal static int[] ExtractRect(int[] plane, int stride, int x0, int y0, int w, int h)
    {
        int[] rect = new int[w * h];
        for (int y = 0; y < h; y++)
        {
            plane.AsSpan(((y0 + y) * stride) + x0, w).CopyTo(rect.AsSpan(y * w, w));
        }

        return rect;
    }
}
