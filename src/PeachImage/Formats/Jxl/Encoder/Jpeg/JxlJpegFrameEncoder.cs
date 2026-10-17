using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Entropy;
using PeachImage.Formats.Jxl.Encoder.Headers;
using PeachImage.Formats.Jxl.Encoder.Modular;
using PeachImage.Formats.Jxl.Encoder.VarDct;
using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Jpeg;
using PeachImage.Formats.Jxl.Modular;
using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Formats.Jxl.Encoder.Jpeg;

/// <summary>
/// Encodes the frame of a losslessly recompressed JPEG: a VarDCT frame without colour transform to XYB whose blocks are the JPEG's own
/// 8x8 DCT blocks. The quantized coefficients are stored as they are (the quantization tables go in a RAW table, so the frame also decodes
/// to the very pixels the JPEG does), the DC image as a small Modular image per DC group and the AC coefficients context-modelled and
/// rANS-coded per 256x256 group. The inverse of the decoder's JPEG capture mode.
/// </summary>
internal static class JxlJpegFrameEncoder
{
    private const int GroupBlocks = 32;
    private const int BlockSize = 64;
    private const int NumQuantTables = 17;
    private const int NumMetaChannels = 4;
    private const int GlobalScale = 1 << 16;
    private const ulong SkipAdaptiveDcSmoothing = 128;

    // JPEG sampling factor modes of the frame header (1x1, 2x2, 2x1, 1x2) and the shifts they imply.
    private static readonly int[] ModeHorizontalShift = [0, 1, 1, 0];
    private static readonly int[] ModeVerticalShift = [0, 1, 0, 1];

    /// <summary>Encodes <paramref name="jpeg"/> as a complete frame (header, table of contents and sections).</summary>
    public static byte[] Encode(JxlJpegData jpeg)
    {
        bool gray = jpeg.Components.Count == 1;
        int[] modes = ChromaModes(jpeg);
        int maxHShift = ModeHorizontalShift[modes[1]];
        int maxVShift = ModeVerticalShift[modes[1]];
        var dims = new JxlFrameDimensions(jpeg.Width, jpeg.Height, JxlFrameHeaderWriter.GroupSizeShift, maxHShift, maxVShift, modular: false, upsampling: 1);
        int blocksX = dims.XSizeBlocks;
        int blocksY = dims.YSizeBlocks;
        bool singleSection = dims.NumGroups == 1;

        // The three channels in frame order: X (Cb), Y, B (Cr). A gray JPEG only has Y; its other two channels are empty.
        int[] hShift = new int[3];
        int[] vShift = new int[3];
        var planes = new short[3][];
        var widths = new int[3];
        for (int c = 0; c < 3; c++)
        {
            hShift[c] = maxHShift - ModeHorizontalShift[modes[c]];
            vShift[c] = maxVShift - ModeVerticalShift[modes[c]];
            widths[c] = blocksX >> hShift[c];
            int height = blocksY >> vShift[c];
            var component = ComponentOf(jpeg, c, gray);
            if (component is null)
            {
                planes[c] = new short[widths[c] * height * BlockSize];
            }
            else if (component.WidthInBlocks != widths[c] || component.HeightInBlocks != height)
            {
                throw new JxlEncodingException("The JPEG's block layout does not match its JPEG XL frame.");
            }
            else
            {
                planes[c] = component.Coefficients;
            }
        }

        var dc = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            dc[c] = new int[planes[c].Length / BlockSize];
            for (int i = 0; i < dc[c].Length; i++)
            {
                dc[c][i] = planes[c][i * BlockSize];
            }
        }

        // Blocks are told apart by whether their luma DC lies above the median; chroma channels get contexts of their own.
        int threshold = MedianOf(dc[1]);
        var blockContext = CreateBlockContext(threshold, gray);
        var tree = JxlMaTreeEncoder.PerChannel([ModularPredictor.Gradient, ModularPredictor.Gradient, ModularPredictor.Gradient, ModularPredictor.Gradient]);

        var acTokens = new JxlTokenList[dims.NumGroups];
        JxlParallel.For(dims.NumGroups, g => acTokens[g] = EncodeAcGroup(dims, g, planes, dc[1], widths, hShift, vShift, blockContext, threshold));

        // DC group and AC-metadata token streams.
        var dcTokens = new JxlTokenList[dims.NumDcGroups];
        var metaTokens = new JxlTokenList[dims.NumDcGroups];
        for (int g = 0; g < dims.NumDcGroups; g++)
        {
            (int x0, int y0, int w, int h) = JxlVarDctFrameEncoder.DcGroupRect(dims, g);
            dcTokens[g] = new JxlTokenList();

            // Modular channel order is Y, X, B, each at its own resolution.
            int[] order = [1, 0, 2];
            for (int m = 0; m < 3; m++)
            {
                int c = order[m];
                int[] rect = JxlVarDctFrameEncoder.ExtractRect(dc[c], widths[c], x0 >> hShift[c], y0 >> vShift[c], w >> hShift[c], h >> vShift[c]);
                JxlModularFrameEncoder.TokenizeChannel(rect, w >> hShift[c], h >> vShift[c], tree.LeafFor(1 + g, m), dcTokens[g]);
            }

            // No chroma-from-luma, every block an 8x8 DCT with the smallest quantization value, no edge filtering.
            metaTokens[g] = new JxlTokenList();
            int tileW = (w + 7) >> 3;
            int tileH = (h + 7) >> 3;
            int count = w * h;
            int metaStream = 1 + (2 * dims.NumDcGroups) + g;
            JxlModularFrameEncoder.TokenizeChannel(new int[tileW * tileH], tileW, tileH, tree.LeafFor(metaStream, 0), metaTokens[g]);
            JxlModularFrameEncoder.TokenizeChannel(new int[tileW * tileH], tileW, tileH, tree.LeafFor(metaStream, 1), metaTokens[g]);
            JxlModularFrameEncoder.TokenizeChannel(new int[count * 2], count, 2, tree.LeafFor(metaStream, 2), metaTokens[g]);
            JxlModularFrameEncoder.TokenizeChannel(new int[count], w, h, tree.LeafFor(metaStream, 3), metaTokens[g]);
        }

        // The quantization table of each channel as the small Modular image the frame header's RAW table is.
        var rawTokens = new JxlTokenList();
        int rawStream = 1 + (3 * dims.NumDcGroups);
        for (int c = 0; c < 3; c++)
        {
            int[] table = RawTable(ComponentOf(jpeg, c, gray) ?? jpeg.Components[0], jpeg);
            JxlModularFrameEncoder.TokenizeChannel(table, 8, 8, tree.LeafFor(rawStream, c), rawTokens);
        }

        // Entropy codes.
        var treeTokens = new JxlTokenList();
        tree.WriteTokens(treeTokens);
        var treePlan = JxlEntropyPlan.Create(JxlMaTreeEncoder.NumTreeContexts, treeTokens);
        var pixelStreams = new List<JxlTokenList>(dcTokens.Length + metaTokens.Length + 1);
        pixelStreams.AddRange(dcTokens);
        pixelStreams.AddRange(metaTokens);
        pixelStreams.Add(rawTokens);
        var pixelPlan = JxlEntropyPlan.Create(tree.LeafCount, pixelStreams);
        var acPlan = JxlEntropyPlan.Create(blockContext.NumAcContexts, acTokens);

        // Global section.
        var global = new JxlBitWriter();
        WriteDcQuantization(global, jpeg, gray);
        WriteQuantizer(global);
        WriteBlockContextMap(global, blockContext);
        WriteCorrelation(global);
        global.WriteBool(true); // has a global MA tree
        treePlan.WriteHeader(global);
        treePlan.WriteStream(global, treeTokens);
        pixelPlan.WriteHeader(global);

        var dcSections = new JxlBitWriter[dims.NumDcGroups];
        for (int g = 0; g < dims.NumDcGroups; g++)
        {
            var writer = singleSection ? global : new JxlBitWriter();
            (_, _, int w, int h) = JxlVarDctFrameEncoder.DcGroupRect(dims, g);
            writer.WriteBits(0, 2); // extra precision
            JxlModularFrameEncoder.WriteStreamHeader(writer, []);
            pixelPlan.WriteStream(writer, dcTokens[g]);
            int upperBound = w * h;
            int bits = upperBound <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(upperBound - 1));
            writer.WriteBits((uint)(upperBound - 1), bits); // every block is its own transform
            JxlModularFrameEncoder.WriteStreamHeader(writer, []);
            pixelPlan.WriteStream(writer, metaTokens[g]);
            dcSections[g] = writer;
        }

        var acGlobal = singleSection ? global : new JxlBitWriter();
        acGlobal.WriteBool(false); // custom dequantization matrices: only the 8x8 one, as a RAW table of the JPEG's quantization values
        for (int i = 0; i < NumQuantTables; i++)
        {
            if (i != 0)
            {
                acGlobal.WriteBits(0, 3); // library default
                continue;
            }

            acGlobal.WriteBits(7, 3); // RAW
            acGlobal.WriteF16(1f / (8 * 255));
            JxlModularFrameEncoder.WriteStreamHeader(acGlobal, []);
            pixelPlan.WriteStream(acGlobal, rawTokens);
        }

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
                sections[groupBase + g] = writer.ToArray();
            });
        }

        var frame = new JxlBitWriter();
        JxlFrameHeaderWriter.Write(frame, new JxlFrameHeaderSpec(Modular: false, XybEncoded: false, NumExtraChannels: 0, ChromaSubsampling: modes, Flags: SkipAdaptiveDcSmoothing));
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

    // The frame header's sampling modes in (Cb, Y, Cr) order: the JPEG's own sampling factor of each channel.
    private static int[] ChromaModes(JxlJpegData jpeg)
    {
        if (jpeg.Components.Count == 1)
        {
            return [0, 0, 0];
        }

        var y = jpeg.Components[0];
        int mode = (y.HorizontalSamplingFactor, y.VerticalSamplingFactor) switch
        {
            (1, 1) => 0,
            (2, 2) => 1,
            (2, 1) => 2,
            (1, 2) => 3,
            _ => throw new JxlEncodingException("The JPEG's sampling factors cannot be stored in a JPEG XL frame header."),
        };
        return [0, mode, 0];
    }

    // Frame channel 0 is Cb, 1 is Y and 2 is Cr; a gray JPEG has Y only.
    private static JpegComponent? ComponentOf(JxlJpegData jpeg, int channel, bool gray) =>
        gray ? (channel == 1 ? jpeg.Components[0] : null) : jpeg.Components[channel == 1 ? 0 : channel == 0 ? 1 : 2];

    private static int MedianOf(int[] values)
    {
        var histogram = new int[4097];
        foreach (int value in values)
        {
            histogram[Math.Clamp(value, -2048, 2048) + 2048]++;
        }

        int half = (values.Length + 1) / 2;
        int seen = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen >= half)
            {
                return i - 2048;
            }
        }

        return 0;
    }

    // Luma blocks get one of two contexts depending on whether their DC is above the threshold; X and B get one each (gray: shared).
    private static BlockContextMap CreateBlockContext(int threshold, bool gray)
    {
        int[][] dcThresholds = [[], [threshold], []];
        byte[] map = new byte[3 * AcStrategy.NumOrders * 2];
        int dctOrder = AcStrategy.OrderOf((int)AcStrategyType.Dct);
        map[((0 * AcStrategy.NumOrders) + dctOrder) * 2] = 0;
        map[(((0 * AcStrategy.NumOrders) + dctOrder) * 2) + 1] = 1;
        for (int d = 0; d < 2; d++)
        {
            map[((((1 * AcStrategy.NumOrders) + dctOrder) * 2) + d)] = 2;
            map[((((2 * AcStrategy.NumOrders) + dctOrder) * 2) + d)] = (byte)(gray ? 2 : 3);
        }

        return BlockContextMap.Create(dcThresholds, [], map);
    }

    private static void WriteBlockContextMap(JxlBitWriter writer, BlockContextMap map)
    {
        writer.WriteBool(false);
        foreach (int[] thresholds in map.DcThresholds)
        {
            writer.WriteBits((uint)thresholds.Length, 4);
            foreach (int threshold in thresholds)
            {
                writer.WriteU32(JxlMaTreeEncoder.PackSigned(threshold), U32Dist.Bits(4), U32Dist.BitsOffset(8, 16), U32Dist.BitsOffset(16, 272), U32Dist.BitsOffset(32, 65808));
            }
        }

        writer.WriteBits((uint)map.QfThresholds.Length, 4);
        foreach (uint threshold in map.QfThresholds)
        {
            writer.WriteU32(threshold - 1, U32Dist.Bits(2), U32Dist.BitsOffset(3, 4), U32Dist.BitsOffset(5, 12), U32Dist.BitsOffset(8, 44));
        }

        JxlEntropyPlan.WriteContextMap(writer, map.ContextMap, map.NumContexts);
    }

    // DC dequantization: a DC coefficient is its quantized value times the table's first entry, in units of 1/(8*255).
    private static void WriteDcQuantization(JxlBitWriter writer, JxlJpegData jpeg, bool gray)
    {
        writer.WriteBool(false);
        for (int c = 0; c < 3; c++)
        {
            var component = ComponentOf(jpeg, c, gray) ?? jpeg.Components[0];
            writer.WriteF16(128f * jpeg.Quant[(int)component.QuantTableIndex].Values[0] / (8 * 255));
        }
    }

    // Global scale of one (so the matrices alone set the step) and DC quantization divisor of one.
    private static void WriteQuantizer(JxlBitWriter writer)
    {
        writer.WriteU32(GlobalScale, U32Dist.BitsOffset(11, 1), U32Dist.BitsOffset(11, 2049), U32Dist.BitsOffset(12, 4097), U32Dist.BitsOffset(16, 8193));
        writer.WriteU32(1, U32Dist.Val(16), U32Dist.BitsOffset(5, 1), U32Dist.BitsOffset(8, 1), U32Dist.BitsOffset(16, 1));
    }

    // The correlation map of a recompressed JPEG: no base correlation, no DC correlation, the default factor.
    private static void WriteCorrelation(JxlBitWriter writer)
    {
        writer.WriteBool(false);
        writer.WriteU32(84, U32Dist.Val(84), U32Dist.Val(256), U32Dist.BitsOffset(8, 2), U32Dist.BitsOffset(16, 258));
        writer.WriteF16(0f);
        writer.WriteF16(0f);
        writer.WriteBits(128, 8);
        writer.WriteBits(128, 8);
    }

    // The quantization values in the order of the decoder's RAW table (the JPEG's block is the transpose of the frame's).
    private static int[] RawTable(JpegComponent component, JxlJpegData jpeg)
    {
        int[] values = jpeg.Quant[(int)component.QuantTableIndex].Values;
        int[] table = new int[BlockSize];
        for (int i = 0; i < BlockSize; i++)
        {
            table[i] = values[((i & 7) * 8) + (i >> 3)];
        }

        return table;
    }

    // ---- AC coefficients -----------------------------------------------------------------------------------------------

    private static JxlTokenList EncodeAcGroup(JxlFrameDimensions dims, int group, short[][] planes, int[] lumaDc, int[] widths, int[] hShift, int[] vShift, BlockContextMap blockContext, int threshold)
    {
        int blocksX = dims.XSizeBlocks;
        int blocksY = dims.YSizeBlocks;
        int bx0 = group % dims.XSizeGroups * GroupBlocks;
        int by0 = group / dims.XSizeGroups * GroupBlocks;
        int groupWidth = Math.Min(GroupBlocks, blocksX - bx0);
        int groupHeight = Math.Min(GroupBlocks, blocksY - by0);

        // The block of a frame is the transpose of the JPEG's: the k-th coefficient of the scan order sits at this JPEG position.
        int[] order = AcStrategy.NaturalOrder((int)AcStrategyType.Dct);
        int[] jpegPosition = new int[BlockSize];
        for (int k = 0; k < BlockSize; k++)
        {
            jpegPosition[k] = ((order[k] & 7) * 8) + (order[k] >> 3);
        }

        var tokens = new JxlTokenList();
        int[][] nonZeroRows = [new int[GroupBlocks * GroupBlocks], new int[GroupBlocks * GroupBlocks], new int[GroupBlocks * GroupBlocks]];
        int dctOrder = AcStrategy.OrderOf((int)AcStrategyType.Dct);
        int[] channelOrder = [1, 0, 2];
        for (int ly = 0; ly < groupHeight; ly++)
        {
            for (int lx = 0; lx < groupWidth; lx++)
            {
                int bucket = lumaDc[((by0 + ly) * blocksX) + bx0 + lx] > threshold ? 1 : 0;
                foreach (int c in channelOrder)
                {
                    if ((lx & ((1 << hShift[c]) - 1)) != 0 || (ly & ((1 << vShift[c]) - 1)) != 0)
                    {
                        continue;
                    }

                    int sbx = lx >> hShift[c];
                    int sby = ly >> vShift[c];
                    int[] nonZeroRow = nonZeroRows[c];
                    int block = ((((by0 + ly) >> vShift[c]) * widths[c]) + ((bx0 + lx) >> hShift[c])) * BlockSize;
                    var coefficients = planes[c].AsSpan(block, BlockSize);

                    int nonZeros = 0;
                    for (int k = 1; k < BlockSize; k++)
                    {
                        if (coefficients[jpegPosition[k]] != 0)
                        {
                            nonZeros++;
                        }
                    }

                    int predicted;
                    if (sbx == 0)
                    {
                        predicted = sby == 0 ? 32 : nonZeroRow[((sby - 1) * GroupBlocks) + sbx];
                    }
                    else if (sby == 0)
                    {
                        predicted = nonZeroRow[(sby * GroupBlocks) + sbx - 1];
                    }
                    else
                    {
                        predicted = (nonZeroRow[((sby - 1) * GroupBlocks) + sbx] + nonZeroRow[(sby * GroupBlocks) + sbx - 1] + 1) / 2;
                    }

                    // Every block has the smallest quantization value, so only the DC bucket and the channel pick the context.
                    int context = blockContext.Context(bucket, 1, dctOrder, c);
                    tokens.Add(blockContext.NonZeroContext(predicted, context), (uint)nonZeros);
                    nonZeroRow[(sby * GroupBlocks) + sbx] = nonZeros;

                    int histogramOffset = blockContext.ZeroDensityContextsOffset(context);
                    int prev = nonZeros > BlockSize / 16 ? 0 : 1;
                    int remaining = nonZeros;
                    for (int k = 1; k < BlockSize && remaining != 0; k++)
                    {
                        int zeroDensity = histogramOffset + ((VarDctFrame.KCoeffNumNonzeroContext[remaining] + VarDctFrame.KCoeffFreqContext[k]) * 2) + prev;
                        uint u = JxlMaTreeEncoder.PackSigned(coefficients[jpegPosition[k]]);
                        tokens.Add(zeroDensity, u);
                        prev = u != 0 ? 1 : 0;
                        remaining -= prev;
                    }
                }
            }
        }

        return tokens;
    }
}
