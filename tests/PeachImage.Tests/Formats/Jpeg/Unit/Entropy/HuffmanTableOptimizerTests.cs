using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Jpeg.Markers;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Entropy;

public class HuffmanTableOptimizerTests
{
    [Fact]
    public void Build_FromSkewedFrequencies_ProducesRoundTrippableCodesForEverySymbol()
    {
        var frequencies = new int[256];

        // A handful of very common DC-like size symbols (0-11)...
        frequencies[0] = 5000;
        frequencies[2] = 3000;
        frequencies[4] = 800;
        frequencies[6] = 50;

        // ...and a mix of common/rare AC-like run/size symbols, including ZRL (0xF0) and EOB (0x00).
        frequencies[0x00] += 2000; // EOB
        frequencies[0xF0] = 10;    // ZRL
        frequencies[0x01] = 4000;
        frequencies[0x11] = 500;
        frequencies[0x22] = 25;
        frequencies[0xA3] = 1;

        AssertRoundTripsAllNonZeroSymbols(frequencies);
    }

    [Fact]
    public void Build_FromPathologicallySkewedFibonacciFrequencies_StillProducesValidTable()
    {
        // A Fibonacci-like frequency sequence across ~250 symbols forces the raw (un-length-limited) Huffman
        // tree depth well past 16 -- the classic pathological input for a package-merge/Kraft-inequality bug.
        // This exercises HuffmanTableOptimizer's Annex K.3 length-limiting redistribution loop; if that loop
        // ever produced an over-subscribed code, HuffmanDecodingTable.Build would throw on the output below.
        var frequencies = new int[256];
        long a = 1, b = 1;
        for (int symbol = 0; symbol < 250; symbol++)
        {
            frequencies[symbol] = (int)Math.Min(a, int.MaxValue / 2);
            (a, b) = (b, a + b);
        }

        AssertRoundTripsAllNonZeroSymbols(frequencies);
    }

    [Fact]
    public void Build_FromSingleNonZeroSymbol_ProducesDegenerateValidTable()
    {
        var frequencies = new int[256];
        frequencies[42] = 1000;

        var (counts, values) = HuffmanTableOptimizer.Build(frequencies);

        Assert.Equal([42], values);

        var encodingTable = HuffmanEncodingTable.Build(counts, values);
        var decodingTable = HuffmanDecodingTable.Build(counts, values);

        using var ms = new MemoryStream();
        var writer = new JpegEntropyWriter(ms);
        encodingTable.Encode(writer, 42);
        writer.Flush();

        // The single symbol must not be coded as all 1-bits (that's the dummy-symbol code, reserved so it's
        // distinguishable from padding/fill bits) -- for a lone symbol its 1-bit code should simply be '0'.
        Assert.Equal(0x00, ms.ToArray()[0] & 0x80);

        ms.Position = 0;
        var reader = new JpegEntropyReader(new JpegByteSource(ms));
        Assert.Equal(42, decodingTable.Decode(reader));
    }

    [Fact]
    public void Build_SumOfCounts_EqualsNumberOfDistinctNonZeroSymbols()
    {
        var frequencies = new int[256];
        frequencies[3] = 10;
        frequencies[7] = 1;
        frequencies[200] = 500;
        frequencies[201] = 500;

        var (counts, values) = HuffmanTableOptimizer.Build(frequencies);

        int totalCodes = 0;
        foreach (byte count in counts)
        {
            totalCodes += count;
        }

        Assert.Equal(4, totalCodes);
        Assert.Equal(4, values.Length);
    }

    private static void AssertRoundTripsAllNonZeroSymbols(int[] frequencies)
    {
        var (counts, values) = HuffmanTableOptimizer.Build(frequencies);

        var encodingTable = HuffmanEncodingTable.Build(counts, values);
        var decodingTable = HuffmanDecodingTable.Build(counts, values);

        foreach (byte symbol in values)
        {
            using var ms = new MemoryStream();
            var writer = new JpegEntropyWriter(ms);
            encodingTable.Encode(writer, symbol);
            writer.Flush();

            ms.Position = 0;
            var reader = new JpegEntropyReader(new JpegByteSource(ms));
            int decoded = decodingTable.Decode(reader);

            Assert.Equal(symbol, decoded);
        }
    }
}
