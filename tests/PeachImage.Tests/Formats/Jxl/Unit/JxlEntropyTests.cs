using PeachImage.Formats.Jxl;
using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Entropy;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlEntropyTests
{
    public static TheoryData<int, int[]> Distributions() => new()
    {
        { 5, [4096] },
        { 5, [2048, 2048] },
        { 5, [1366, 1365, 1365] },
        { 6, [1, 4095] },
        { 6, [3000, 1, 1, 1, 1, 1000, 92] },
        { 7, [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180, 190, 200, 210, 220, 230, 240, 250, 260, 270, 280, 290, 300, 310, 320, 330, 340, 350, 360, 370, 380, 390, 400, 410, 420, 430, 440, 450, 460, 470, 480, 490, 500, 510, 520, 530, 540, 550, 560, 570, 580, 590, 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800] },
        { 8, [1, 1, 1, 1, 4092] },
    };

    [Theory]
    [MemberData(nameof(Distributions))]
    public void AliasTable_MapsEveryValueToExactlyFrequencyManyOffsets(int logAlphaSize, int[] distribution)
    {
        // Rescale so the histogram sums to the 4096-entry table.
        int[] counts = Normalize(distribution);
        var table = new AliasEntry[1 << logAlphaSize];
        JxlAliasTable.Init(counts, logAlphaSize, table);

        int logEntrySize = JxlAliasTable.LogTableSize - logAlphaSize;
        int entrySizeMinusOne = (1 << logEntrySize) - 1;
        var seen = new HashSet<(int Symbol, int Offset)>();
        var perSymbol = new int[1 << logAlphaSize];

        for (int value = 0; value < JxlAliasTable.TableSize; value++)
        {
            var (symbol, offset, frequency) = JxlAliasTable.Lookup(table, value, logEntrySize, entrySizeMinusOne);

            Assert.Equal(counts[symbol], frequency);
            Assert.InRange(offset, 0, frequency - 1);
            Assert.True(seen.Add((symbol, offset)), $"({symbol}, {offset}) was produced twice");
            perSymbol[symbol]++;
        }

        for (int symbol = 0; symbol < counts.Length; symbol++)
        {
            Assert.Equal(counts[symbol], perSymbol[symbol]);
        }
    }

    private static int[] Normalize(int[] distribution)
    {
        int sum = distribution.Sum();
        if (sum == JxlAliasTable.TableSize)
        {
            return distribution;
        }

        var scaled = distribution.Select(d => Math.Max(1, d * JxlAliasTable.TableSize / sum)).ToArray();
        int largest = Array.IndexOf(scaled, scaled.Max());
        scaled[largest] += JxlAliasTable.TableSize - scaled.Sum();
        return scaled;
    }

    [Fact]
    public void AliasTable_RejectsHistogramThatDoesNotSumToTableSize()
    {
        var table = new AliasEntry[32];
        Assert.Throws<JxlDecodingException>(() => JxlAliasTable.Init([100, 100], 5, table));
    }

    [Fact]
    public void PrefixCode_DecodesCodesLongerThanTheRootTable()
    {
        // Lengths 1..15 for symbols 0..15 plus a second 15-bit code for symbol 16 satisfy Kraft's equality, and
        // codes over 8 bits long exercise the second-level tables.
        int[] lengths = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15];
        byte[] codeLengths = new byte[lengths.Length];
        Span<ushort> counts = stackalloc ushort[16];
        for (int i = 0; i < lengths.Length; i++)
        {
            codeLengths[i] = (byte)lengths[i];
            counts[lengths[i]]++;
        }

        var table = new HuffmanEntry[lengths.Length + 376];
        int size = JxlHuffmanCode.BuildTable(table, JxlHuffmanCode.RootBits, codeLengths, lengths.Length, counts);
        Assert.True(size > 256);

        // Canonical code assignment: ordered by length, then symbol.
        var writer = new JxlBitWriter();
        uint code = 0;
        var codes = new uint[lengths.Length];
        for (int len = 1, symbol = 0; len <= 15; len++)
        {
            for (int s = 0; s < lengths.Length; s++)
            {
                if (lengths[s] == len)
                {
                    codes[s] = code++;
                    symbol++;
                }
            }

            code <<= 1;
        }

        int[] message = [15, 0, 14, 8, 3, 9, 15, 1, 13, 7, 12, 2, 11, 5, 10, 4, 6, 0, 0, 14];
        foreach (int symbol in message)
        {
            writer.WriteCodeMsbFirst(codes[symbol], lengths[symbol]);
        }

        // Decode through a tiny wrapper code that reuses the table.
        byte[] data = writer.ToArray();
        var reader = new JxlBitReader(data);
        var decoded = new List<int>();
        for (int i = 0; i < message.Length; i++)
        {
            decoded.Add(ReadSymbol(table, ref reader));
        }

        Assert.Equal(message, decoded);
        Assert.False(reader.IsOverrun);
    }

    // Mirrors JxlHuffmanCode.ReadSymbol over a raw table.
    private static int ReadSymbol(HuffmanEntry[] table, ref JxlBitReader br)
    {
        int index = (int)br.PeekBits(JxlHuffmanCode.RootBits);
        var entry = table[index];
        int bits = entry.Bits;
        if (bits > JxlHuffmanCode.RootBits)
        {
            br.Consume(JxlHuffmanCode.RootBits);
            bits -= JxlHuffmanCode.RootBits;
            index += entry.Value;
            index += (int)br.PeekBits(bits);
            entry = table[index];
        }

        br.Consume(entry.Bits);
        return entry.Value;
    }

    [Fact]
    public void PrefixCode_SimpleCodeWithTwoSymbols()
    {
        // simple-code marker (01), 2 symbols (num_symbols-1 = 1), alphabet 8 → 3 bits per symbol: 5 then 2.
        var writer = new JxlBitWriter();
        writer.WriteBits(1, 2);
        writer.WriteBits(1, 2);
        writer.WriteBits(5, 3);
        writer.WriteBits(2, 3);

        // Then data: with symbols sorted (2, 5) the codes are 0 → 2 and 1 → 5.
        writer.WriteBits(0b1, 1);
        writer.WriteBits(0b0, 1);
        writer.WriteBits(0b1, 1);

        var reader = new JxlBitReader(writer.ToArray());
        var code = JxlHuffmanCode.Read(8, ref reader);

        Assert.NotNull(code);
        Assert.Equal(5, code.ReadSymbol(ref reader));
        Assert.Equal(2, code.ReadSymbol(ref reader));
        Assert.Equal(5, code.ReadSymbol(ref reader));
    }

    [Fact]
    public void PrefixCode_DuplicateSimpleSymbols_AreRejected()
    {
        var writer = new JxlBitWriter();
        writer.WriteBits(1, 2);
        writer.WriteBits(1, 2);
        writer.WriteBits(3, 3);
        writer.WriteBits(3, 3);

        var reader = new JxlBitReader(writer.ToArray());
        Assert.Null(JxlHuffmanCode.Read(8, ref reader));
    }

    [Theory]
    [InlineData(4, 2, 0, 0u, 0u)]
    [InlineData(4, 2, 0, 15u, 15u)]
    public void HybridUint_BelowSplitToken_IsTheTokenItself(int split, int msb, int lsb, uint token, uint expected)
    {
        var config = new HybridUintConfig(split, msb, lsb);
        var reader = new JxlBitReader(new byte[8]);

        Assert.Equal(expected, config.Decode(token, ref reader));
        Assert.Equal(0, reader.BitPosition);
    }

    [Fact]
    public void HybridUint_AboveSplitToken_ReadsRawBitsAndRebuildsValue()
    {
        // split_exponent 4, 2 msb, 0 lsb: token 16 = n 4, msb bits '00', 2 raw bits. N=17 is token 16 + raw '01'.
        var config = new HybridUintConfig(4, 2, 0);
        var writer = new JxlBitWriter();
        writer.WriteBits(0b01, 2);
        var reader = new JxlBitReader(writer.ToArray());

        Assert.Equal(17u, config.Decode(16, ref reader));
        Assert.Equal(2, reader.BitPosition);

        // 65535 → token 63 with 13 raw one-bits.
        var big = new JxlBitWriter();
        big.WriteBits(0x1FFF, 13);
        var bigReader = new JxlBitReader(big.ToArray());
        Assert.Equal(65535u, config.Decode(63, ref bigReader));
    }

    [Fact]
    public void HybridUint_WithLsbInToken_PlacesLowBitsLast()
    {
        // split_exponent 4, msb 1, lsb 1: token 16+... decode value 0b110101 (53): n=5, m=21=10101.
        // token = 16 + ((5-4) << 2) + ((m >> (5-1)) << 1) + (m & 1) = 16 + 4 + 2 + 1 = 23; raw bits = (53 >> 1) & ((1<<3)-1) = 2.
        var config = new HybridUintConfig(4, 1, 1);
        var writer = new JxlBitWriter();
        writer.WriteBits(2, 3);
        var reader = new JxlBitReader(writer.ToArray());

        Assert.Equal(53u, config.Decode(23, ref reader));
    }
}
