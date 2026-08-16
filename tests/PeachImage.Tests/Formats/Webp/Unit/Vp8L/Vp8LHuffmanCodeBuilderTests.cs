using PeachImage.Formats.Webp.Decoding.Vp8L;
using PeachImage.Formats.Webp.Encoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

/// <summary>
/// Correctness tests for <see cref="Vp8LHuffmanCodeBuilder"/> — the encoder-side counterpart of
/// <see cref="Vp8LHuffmanTableBuilder"/>. The existing, already-correct decode-side table builder is used as
/// the primary oracle throughout: it throws on any Kraft violation or over/under-subscription, so feeding it
/// this builder's output is a strong, cheap correctness net.
/// </summary>
public class Vp8LHuffmanCodeBuilderTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(50)]
    [InlineData(280)]
    [InlineData(2327)] // Max green alphabet size: 256 literal + 24 length + 2048 max color cache.
    public void BuildCodeLengths_ProducesAValidCanonicalTree_ForRandomHistograms(int alphabetSize)
    {
        var random = new Random(alphabetSize);
        var freq = new int[alphabetSize];
        for (int i = 0; i < alphabetSize; i++)
        {
            // Mostly zero with a few large, skewed values -- stresses the length-limiting repair path.
            freq[i] = random.Next(0, 20) == 0 ? random.Next(1, 100_000) : 0;
        }

        if (freq.All(f => f == 0))
        {
            freq[0] = 1;
        }

        var codeLengths = new int[alphabetSize];
        Vp8LHuffmanCodeBuilder.BuildCodeLengths(freq, codeLengths, maxLength: 15);

        Vp8LHuffmanTableBuilder.Build(codeLengths, rootBits: 8);
        Assert.True(codeLengths.Max() <= 15);
    }

    [Fact]
    public void BuildCodeLengths_AllZeroFrequency_DeclaresSymbolZero()
    {
        var freq = new int[10];
        var codeLengths = new int[10];

        Vp8LHuffmanCodeBuilder.BuildCodeLengths(freq, codeLengths, maxLength: 15);

        Assert.Equal(1, codeLengths[0]);
        Assert.True(codeLengths.Skip(1).All(l => l == 0));
    }

    [Fact]
    public void BuildCodeLengths_SingleUsedSymbol_GetsLengthOne()
    {
        var freq = new int[10];
        freq[4] = 42;
        var codeLengths = new int[10];

        Vp8LHuffmanCodeBuilder.BuildCodeLengths(freq, codeLengths, maxLength: 15);

        Assert.Equal(1, codeLengths[4]);
        Assert.True(codeLengths.Where((_, i) => i != 4).All(l => l == 0));
    }

    [Fact]
    public void BuildCodeLengths_RespectsATighterMaxLength_ForTheCodeLengthAlphabet()
    {
        var random = new Random(99);
        var freq = new int[19];
        for (int i = 0; i < 19; i++)
        {
            freq[i] = random.Next(0, 20) == 0 ? random.Next(1, 1_000_000) : 1;
        }

        var codeLengths = new int[19];
        Vp8LHuffmanCodeBuilder.BuildCodeLengths(freq, codeLengths, maxLength: 7);

        Assert.True(codeLengths.Max() <= 7);
        Vp8LHuffmanTableBuilder.Build(codeLengths, rootBits: 7);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(50)]
    [InlineData(280)]
    public void AssignCanonicalCodes_RoundTrips_ThroughRealBitReaderAndDecodeTable(int alphabetSize)
    {
        var random = new Random(alphabetSize + 1);
        var freq = new int[alphabetSize];
        for (int i = 0; i < alphabetSize; i++)
        {
            freq[i] = random.Next(0, 5) == 0 ? random.Next(1, 1000) : 0;
        }

        if (freq.All(f => f == 0))
        {
            freq[0] = 1;
        }

        var codeLengths = new int[alphabetSize];
        Vp8LHuffmanCodeBuilder.BuildCodeLengths(freq, codeLengths, maxLength: 15);

        var codes = new uint[alphabetSize];
        Vp8LHuffmanCodeBuilder.AssignCanonicalCodes(codeLengths, codes);

        var table = Vp8LHuffmanTableBuilder.Build(codeLengths, rootBits: 8);

        for (int symbol = 0; symbol < alphabetSize; symbol++)
        {
            if (codeLengths[symbol] == 0)
            {
                continue;
            }

            var writer = new Vp8LBitWriter();
            writer.WriteBits(codes[symbol], codeLengths[symbol]);
            byte[] bytes = writer.ToArray();
            var reader = new Vp8LBitReader(bytes, 0, bytes.Length);

            int decoded = table.Decode(reader);
            Assert.Equal(symbol, decoded);
        }
    }
}
