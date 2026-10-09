using System.Numerics;
using PeachImage.Formats.Jxl.Bitstream;

namespace PeachImage.Formats.Jxl.Entropy;

/// <summary>
/// How a decoded token is turned into an integer: tokens below <see cref="SplitToken"/> are the value itself;
/// larger tokens carry the bit count, <see cref="MsbInToken"/> leading and <see cref="LsbInToken"/> trailing bits,
/// with the remaining bits read raw from the stream.
/// </summary>
internal readonly struct HybridUintConfig
{
    public HybridUintConfig(int splitExponent, int msbInToken, int lsbInToken)
    {
        SplitExponent = splitExponent;
        SplitToken = 1u << splitExponent;
        MsbInToken = msbInToken;
        LsbInToken = lsbInToken;
    }

    public int SplitExponent { get; }

    public uint SplitToken { get; }

    public int MsbInToken { get; }

    public int LsbInToken { get; }

    /// <summary>Reads the configuration from the bitstream for an alphabet of <c>1 &lt;&lt; logAlphaSize</c> symbols.</summary>
    public static HybridUintConfig Read(int logAlphaSize, ref JxlBitReader br)
    {
        int splitExponent = (int)br.ReadBits(CeilLog2Nonzero((uint)logAlphaSize + 1));
        int msb = 0;
        int lsb = 0;
        if (splitExponent != logAlphaSize)
        {
            msb = (int)br.ReadBits(CeilLog2Nonzero((uint)splitExponent + 1));
            if (msb > splitExponent)
            {
                throw new JxlDecodingException("Invalid hybrid-integer configuration.");
            }

            lsb = (int)br.ReadBits(CeilLog2Nonzero((uint)(splitExponent - msb) + 1));
        }

        if (lsb + msb > splitExponent)
        {
            throw new JxlDecodingException("Invalid hybrid-integer configuration.");
        }

        return new HybridUintConfig(splitExponent, msb, lsb);
    }

    /// <summary>Turns a decoded <paramref name="token"/> into its value, reading any extra raw bits from <paramref name="br"/>.</summary>
    public uint Decode(uint token, ref JxlBitReader br)
    {
        if (token < SplitToken)
        {
            return token;
        }

        int shift = MsbInToken + LsbInToken;

        // More than 29 raw bits cannot be valid; mask rather than fail, as the reference decoder does.
        int bits = (SplitExponent - shift + (int)((token - SplitToken) >> shift)) & 31;
        uint low = token & ((1u << LsbInToken) - 1);
        token >>= LsbInToken;
        uint raw = br.ReadBits(bits);
        ulong value = ((((1ul << MsbInToken) | (token & ((1u << MsbInToken) - 1))) << bits) | raw) << LsbInToken | low;
        return (uint)value;
    }

    private static int CeilLog2Nonzero(uint x) => x <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount(x - 1);
}

/// <summary>LZ77 settings of an entropy-coded stream: values at or above <see cref="MinSymbol"/> are run lengths.</summary>
internal struct Lz77Params
{
    public bool Enabled;
    public uint MinSymbol;
    public uint MinLength;
    public HybridUintConfig LengthConfig;

    /// <summary>The (clustered) context that decodes distances.</summary>
    public int DistanceContext;

    public static Lz77Params Read(ref JxlBitReader br)
    {
        var result = default(Lz77Params);
        result.Enabled = br.ReadBool();
        if (!result.Enabled)
        {
            return result;
        }

        result.MinSymbol = JxlFieldReader.ReadU32(ref br, U32Dist.Val(224), U32Dist.Val(512), U32Dist.Val(4096), U32Dist.BitsOffset(15, 8));
        result.MinLength = JxlFieldReader.ReadU32(ref br, U32Dist.Val(3), U32Dist.Val(4), U32Dist.BitsOffset(2, 5), U32Dist.BitsOffset(8, 9));
        return result;
    }
}

/// <summary>
/// A decoded set of entropy-coding histograms plus the context map that routes contexts to them: either rANS
/// alias tables or prefix codes, with optional LZ77. Immutable once read; a <see cref="JxlSymbolReader"/> decodes from it.
/// </summary>
internal sealed class JxlEntropyCode
{
    private const int MaxPrefixBits = 15;
    private const int MaxAnsAlphabetSize = 256;
    private const int MaxClusters = 256;

    // Brotli-derived table for the 7-bit prefix of an ANS histogram's log-count symbols: {bits, symbol}.
    private static readonly byte[] LogCountTable =
    [
        3, 10, 7, 12, 3, 7, 4, 3, 3, 6, 3, 8, 3, 9, 4, 5,
        3, 10, 4, 4, 3, 7, 4, 1, 3, 6, 3, 8, 3, 9, 4, 2,
        3, 10, 5, 0, 3, 7, 4, 3, 3, 6, 3, 8, 3, 9, 4, 5,
        3, 10, 4, 4, 3, 7, 4, 1, 3, 6, 3, 8, 3, 9, 4, 2,
        3, 10, 6, 11, 3, 7, 4, 3, 3, 6, 3, 8, 3, 9, 4, 5,
        3, 10, 4, 4, 3, 7, 4, 1, 3, 6, 3, 8, 3, 9, 4, 2,
        3, 10, 5, 0, 3, 7, 4, 3, 3, 6, 3, 8, 3, 9, 4, 5,
        3, 10, 4, 4, 3, 7, 4, 1, 3, 6, 3, 8, 3, 9, 4, 2,
        3, 10, 7, 13, 3, 7, 4, 3, 3, 6, 3, 8, 3, 9, 4, 5,
        3, 10, 4, 4, 3, 7, 4, 1, 3, 6, 3, 8, 3, 9, 4, 2,
        3, 10, 5, 0, 3, 7, 4, 3, 3, 6, 3, 8, 3, 9, 4, 5,
        3, 10, 4, 4, 3, 7, 4, 1, 3, 6, 3, 8, 3, 9, 4, 2,
        3, 10, 6, 11, 3, 7, 4, 3, 3, 6, 3, 8, 3, 9, 4, 5,
        3, 10, 4, 4, 3, 7, 4, 1, 3, 6, 3, 8, 3, 9, 4, 2,
        3, 10, 5, 0, 3, 7, 4, 3, 3, 6, 3, 8, 3, 9, 4, 5,
        3, 10, 4, 4, 3, 7, 4, 1, 3, 6, 3, 8, 3, 9, 4, 2,
    ];

    private JxlEntropyCode()
    {
    }

    public bool UsePrefixCode { get; private set; }

    public int LogAlphaSize { get; private set; }

    public Lz77Params Lz77 { get; private set; }

    public HybridUintConfig[] UintConfigs { get; private set; } = [];

    internal JxlHuffmanCode[] Huffman { get; private set; } = [];

    /// <summary>
    /// For rANS codes, per histogram: the only symbol it can produce if its distribution has a single nonzero entry,
    /// otherwise -1. Prefix codes report -1.
    /// </summary>
    internal int[] DegenerateSymbols { get; private set; } = [];

    /// <summary>All alias tables back to back, each <c>1 &lt;&lt; LogAlphaSize</c> entries long.</summary>
    internal AliasEntry[] AliasTables { get; private set; } = [];

    /// <summary>
    /// Reads histograms for <paramref name="numContexts"/> contexts. <paramref name="contextMap"/> receives the
    /// context-to-histogram map (one extra trailing entry when LZ77 is enabled, for the distance context).
    /// </summary>
    public static JxlEntropyCode Read(ref JxlBitReader br, int numContexts, out byte[] contextMap, bool disallowLz77 = false)
    {
        var code = new JxlEntropyCode();
        var lz77 = Lz77Params.Read(ref br);
        if (lz77.Enabled)
        {
            numContexts++;
            lz77.LengthConfig = HybridUintConfig.Read(8, ref br);
            if (disallowLz77)
            {
                throw new JxlDecodingException("LZ77 is not allowed here.");
            }
        }

        int numHistograms = 1;
        contextMap = new byte[numContexts];
        if (numContexts > 1)
        {
            numHistograms = ReadContextMap(ref br, contextMap);
        }

        lz77.DistanceContext = contextMap[^1];
        code.Lz77 = lz77;
        code.UsePrefixCode = br.ReadBool();
        code.LogAlphaSize = code.UsePrefixCode ? MaxPrefixBits : (int)br.ReadBits(2) + 5;

        var configs = new HybridUintConfig[numHistograms];
        for (int i = 0; i < configs.Length; i++)
        {
            configs[i] = HybridUintConfig.Read(code.LogAlphaSize, ref br);
        }

        code.UintConfigs = configs;
        code.ReadCodes(ref br, numHistograms);
        br.ThrowIfOverrun();
        return code;
    }

    private void ReadCodes(ref JxlBitReader br, int numHistograms)
    {
        int maxAlphabetSize = 1 << LogAlphaSize;
        if (UsePrefixCode)
        {
            var sizes = new int[numHistograms];
            for (int c = 0; c < sizes.Length; c++)
            {
                sizes[c] = DecodeVarLenUint16(ref br) + 1;
                if (sizes[c] > maxAlphabetSize)
                {
                    throw new JxlDecodingException($"Prefix-code alphabet size {sizes[c]} is too large.");
                }
            }

            var codes = new JxlHuffmanCode[numHistograms];
            for (int c = 0; c < codes.Length; c++)
            {
                codes[c] = sizes[c] > 1
                    ? JxlHuffmanCode.Read(sizes[c], ref br) ?? throw new JxlDecodingException($"Invalid prefix code {c}.")
                    : JxlHuffmanCode.SingleSymbol();
                br.ThrowIfOverrun();
            }

            Huffman = codes;
            DegenerateSymbols = Enumerable.Repeat(-1, numHistograms).ToArray();
            return;
        }

        if (maxAlphabetSize > MaxAnsAlphabetSize)
        {
            throw new JxlDecodingException("The ANS alphabet is too large.");
        }

        var tables = new AliasEntry[numHistograms << LogAlphaSize];
        var degenerate = new int[numHistograms];
        for (int c = 0; c < numHistograms; c++)
        {
            int[] counts = ReadHistogram(ref br);
            br.ThrowIfOverrun();
            if (counts.Length > maxAlphabetSize)
            {
                throw new JxlDecodingException($"ANS alphabet size {counts.Length} is too large.");
            }

            int length = counts.Length;
            while (length > 0 && counts[length - 1] == 0)
            {
                length--;
            }

            int degenerateSymbol = length == 0 ? 0 : length - 1;
            for (int s = 0; s < degenerateSymbol; s++)
            {
                if (counts[s] != 0)
                {
                    degenerateSymbol = -1;
                    break;
                }
            }

            degenerate[c] = degenerateSymbol;
            JxlAliasTable.Init(counts, LogAlphaSize, tables.AsSpan(c << LogAlphaSize, 1 << LogAlphaSize));
        }

        AliasTables = tables;
        DegenerateSymbols = degenerate;
    }

    // A number in [0, 255], using 1 to 11 bits.
    private static int DecodeVarLenUint8(ref JxlBitReader br)
    {
        if (br.ReadBool())
        {
            int nbits = (int)br.ReadBits(3);
            return nbits == 0 ? 1 : (int)br.ReadBits(nbits) + (1 << nbits);
        }

        return 0;
    }

    // A number in [0, 65535], using 1 to 21 bits.
    private static int DecodeVarLenUint16(ref JxlBitReader br)
    {
        if (br.ReadBool())
        {
            int nbits = (int)br.ReadBits(4);
            return nbits == 0 ? 1 : (int)br.ReadBits(nbits) + (1 << nbits);
        }

        return 0;
    }

    private static int[] ReadHistogram(ref JxlBitReader br)
    {
        const int logTable = JxlAliasTable.LogTableSize;
        const int range = 1 << logTable;

        if (br.ReadBool())
        {
            // Simple code: one or two symbols.
            int numSymbols = (int)br.ReadBits(1) + 1;
            Span<int> symbols = stackalloc int[2];
            int maxSymbol = 0;
            for (int i = 0; i < numSymbols; i++)
            {
                symbols[i] = DecodeVarLenUint8(ref br);
                maxSymbol = Math.Max(maxSymbol, symbols[i]);
            }

            var simple = new int[maxSymbol + 1];
            if (numSymbols == 1)
            {
                simple[symbols[0]] = range;
            }
            else
            {
                if (symbols[0] == symbols[1])
                {
                    throw new JxlDecodingException("Corrupt ANS histogram.");
                }

                simple[symbols[0]] = (int)br.ReadBits(logTable);
                simple[symbols[1]] = range - simple[symbols[0]];
            }

            return simple;
        }

        if (br.ReadBool())
        {
            // Flat histogram.
            int alphabetSize = DecodeVarLenUint8(ref br) + 1;
            if (alphabetSize > range)
            {
                throw new JxlDecodingException("Corrupt ANS histogram.");
            }

            var flat = new int[alphabetSize];
            int each = range / alphabetSize;
            int remainder = range % alphabetSize;
            for (int i = 0; i < flat.Length; i++)
            {
                flat[i] = each + (i < remainder ? 1 : 0);
            }

            return flat;
        }

        // General histogram: precision shift, then log-counts with RLE, then mantissa bits.
        int upperBoundLog = 3; // FloorLog2(LogTableSize + 1)
        int log = 0;
        for (; log < upperBoundLog; log++)
        {
            if (!br.ReadBool())
            {
                break;
            }
        }

        int shift = (int)(br.ReadBits(log) | (1u << log)) - 1;
        if (shift > logTable + 1)
        {
            throw new JxlDecodingException("Invalid ANS histogram shift.");
        }

        int length = DecodeVarLenUint8(ref br) + 3;
        var counts = new int[length];
        var logCounts = new int[length];
        var same = new int[length];
        int omitLog = -1;
        int omitPos = -1;
        for (int i = 0; i < length; i++)
        {
            int index = (int)br.PeekBits(7);
            br.Consume(LogCountTable[index * 2]);
            logCounts[i] = LogCountTable[(index * 2) + 1] - 1;
            if (logCounts[i] == logTable)
            {
                int rleLength = DecodeVarLenUint8(ref br);
                same[i] = rleLength + 5;
                i += rleLength + 3;
                continue;
            }

            if (logCounts[i] > omitLog)
            {
                omitLog = logCounts[i];
                omitPos = i;
            }
        }

        if (omitPos < 0)
        {
            throw new JxlDecodingException("Invalid ANS histogram.");
        }

        if (omitPos + 1 < length && logCounts[omitPos + 1] == logTable)
        {
            throw new JxlDecodingException("Invalid ANS histogram.");
        }

        int total = 0;
        int previous = 0;
        int numSame = 0;
        for (int i = 0; i < length; i++)
        {
            if (same[i] != 0)
            {
                numSame = same[i] - 1;
                previous = i > 0 ? counts[i - 1] : 0;
            }

            if (numSame > 0)
            {
                counts[i] = previous;
                numSame--;
            }
            else
            {
                int code = logCounts[i];
                if (i == omitPos || code < 0)
                {
                    continue;
                }

                if (shift == 0 || code == 0)
                {
                    counts[i] = 1 << code;
                }
                else
                {
                    int bitCount = PopulationCountPrecision(code, shift);
                    counts[i] = (1 << code) + ((int)br.ReadBits(bitCount) << (code - bitCount));
                }
            }

            total += counts[i];
        }

        counts[omitPos] = range - total;
        if (counts[omitPos] <= 0)
        {
            throw new JxlDecodingException("Invalid ANS histogram count.");
        }

        return counts;
    }

    // The number of mantissa bits stored for a count whose floor(log2) is logCount.
    private static int PopulationCountPrecision(int logCount, int shift)
    {
        int r = Math.Min(logCount, shift - ((JxlAliasTable.LogTableSize - logCount) >> 1));
        return r < 0 ? 0 : r;
    }

    /// <summary>Reads a context map of <paramref name="contextMap"/>.Length entries; returns the number of histograms it references.</summary>
    internal static int ReadContextMap(ref JxlBitReader br, byte[] contextMap)
    {
        bool isSimple = br.ReadBool();
        if (isSimple)
        {
            int bitsPerEntry = (int)br.ReadBits(2);
            if (bitsPerEntry != 0)
            {
                for (int i = 0; i < contextMap.Length; i++)
                {
                    contextMap[i] = (byte)br.ReadBits(bitsPerEntry);
                }
            }
        }
        else
        {
            bool useMtf = br.ReadBool();

            // A context map has a single context of its own; LZ77 is not allowed for tiny maps.
            var code = Read(ref br, 1, out var sinkContextMap, disallowLz77: contextMap.Length <= 2);
            using var reader = new JxlSymbolReader(code, ref br);
            uint maxSymbol = 0;
            for (int i = 0; i < contextMap.Length; i++)
            {
                uint symbol = reader.ReadHybridUint(sinkContextMap[0], ref br);
                maxSymbol = Math.Max(maxSymbol, symbol);
                if (symbol >= MaxClusters)
                {
                    throw new JxlDecodingException("Invalid cluster ID.");
                }

                contextMap[i] = (byte)symbol;
            }

            if (!reader.CheckFinalState())
            {
                throw new JxlDecodingException("Invalid context map.");
            }

            if (useMtf)
            {
                InverseMoveToFront(contextMap);
            }
        }

        br.ThrowIfOverrun();

        int numHistograms = 0;
        foreach (byte entry in contextMap)
        {
            numHistograms = Math.Max(numHistograms, entry + 1);
        }

        // Every histogram index below the maximum must be used.
        Span<bool> used = stackalloc bool[MaxClusters];
        used.Clear();
        int found = 0;
        foreach (byte entry in contextMap)
        {
            if (!used[entry])
            {
                used[entry] = true;
                found++;
            }
        }

        if (found != numHistograms)
        {
            throw new JxlDecodingException("Incomplete context map.");
        }

        return numHistograms;
    }

    private static void InverseMoveToFront(Span<byte> values)
    {
        Span<byte> mtf = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
        {
            mtf[i] = (byte)i;
        }

        for (int i = 0; i < values.Length; i++)
        {
            int index = values[i];
            byte value = mtf[index];
            values[i] = value;
            if (index != 0)
            {
                mtf[..index].CopyTo(mtf.Slice(1, index));
                mtf[0] = value;
            }
        }
    }
}
