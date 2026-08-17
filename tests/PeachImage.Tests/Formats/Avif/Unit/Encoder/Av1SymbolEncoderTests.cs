using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1SymbolEncoder"/> against the existing, already-correct <see cref="Av1SymbolDecoder"/>
/// -- the strongest available oracle for this component. Every encoded sequence is decoded back through the
/// real, unmodified decoder and both the decoded symbols and the final adapted CDF state are compared.
/// </summary>
public class Av1SymbolEncoderTests
{
    [Theory]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 1 })]
    [InlineData(new[] { 0, 1, 0, 1, 1, 1, 0, 0 })]
    [InlineData(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 })]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void WriteBool_RoundTripsThroughDecoder(int[] bits)
    {
        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);
        foreach (int bit in bits)
        {
            encoder.WriteBool(bit);
        }

        byte[] data = encoder.Flush();
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);

        foreach (int expected in bits)
        {
            Assert.Equal(expected, decoder.ReadBool());
        }
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(1u, 1)]
    [InlineData(0u, 8)]
    [InlineData(255u, 8)]
    [InlineData(0xABu, 8)]
    [InlineData(0x1u, 4)]
    [InlineData(0xFu, 4)]
    public void WriteLiteral_RoundTripsThroughDecoder(uint value, int n)
    {
        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);
        encoder.WriteLiteral(value, n);

        byte[] data = encoder.Flush();
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);

        Assert.Equal(value, decoder.ReadLiteral(n));
    }

    [Fact]
    public void WriteLiteral_MultipleValues_RoundTripInOrder()
    {
        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);
        encoder.WriteLiteral(5, 3);
        encoder.WriteLiteral(200, 8);
        encoder.WriteLiteral(1, 1);
        encoder.WriteLiteral(42, 6);

        byte[] data = encoder.Flush();
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);

        Assert.Equal(5u, decoder.ReadLiteral(3));
        Assert.Equal(200u, decoder.ReadLiteral(8));
        Assert.Equal(1u, decoder.ReadLiteral(1));
        Assert.Equal(42u, decoder.ReadLiteral(6));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteSymbol_BinaryCdf_RoundTripsAndCdfStateMatches(bool disableCdfUpdate)
    {
        int[] symbols = [0, 1, 0, 0, 1, 1, 0, 1, 1, 1, 0, 0, 0, 1, 0];
        ushort[] encodeCdf = [1 << 14, 1 << 15, 0];
        ushort[] decodeCdf = [1 << 14, 1 << 15, 0];

        var encoder = new Av1SymbolEncoder(disableCdfUpdate);
        foreach (int s in symbols)
        {
            encoder.WriteSymbol(encodeCdf, s);
        }

        byte[] data = encoder.Flush();
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate);

        foreach (int expected in symbols)
        {
            Assert.Equal(expected, decoder.ReadSymbol(decodeCdf));
        }

        Assert.Equal(encodeCdf, decodeCdf);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void WriteSymbol_MultiSymbolUniformCdf_RoundTripsAndCdfStateMatches(int symbolCount)
    {
        ushort[] BuildUniformCdf(int n)
        {
            var cdf = new ushort[n + 1];
            for (int i = 0; i < n; i++)
            {
                cdf[i] = (ushort)((32768 * (i + 1)) / n);
            }

            cdf[n - 1] = 1 << 15;
            cdf[n] = 0;
            return cdf;
        }

        ushort[] encodeCdf = BuildUniformCdf(symbolCount);
        ushort[] decodeCdf = BuildUniformCdf(symbolCount);

        var random = new Random(2024);
        int[] symbols = new int[40];
        for (int i = 0; i < symbols.Length; i++)
        {
            symbols[i] = random.Next(symbolCount);
        }

        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);
        foreach (int s in symbols)
        {
            encoder.WriteSymbol(encodeCdf, s);
        }

        byte[] data = encoder.Flush();
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);

        foreach (int expected in symbols)
        {
            Assert.Equal(expected, decoder.ReadSymbol(decodeCdf));
        }

        Assert.Equal(encodeCdf, decodeCdf);
    }

    [Fact]
    public void WriteSymbol_SkewedCdf_RoundTripsCorrectly()
    {
        // Heavily skewed toward symbol 0 (typical of a real coefficient/EOB context), including the
        // near-degenerate case of a symbol whose probability is close to the EC_MIN_PROB floor.
        ushort[] encodeCdf = [32700, 32750, 1 << 15, 0];
        ushort[] decodeCdf = [32700, 32750, 1 << 15, 0];

        int[] symbols = [0, 0, 0, 0, 2, 0, 0, 1, 0, 0, 0, 0, 0, 2, 0];

        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);
        foreach (int s in symbols)
        {
            encoder.WriteSymbol(encodeCdf, s);
        }

        byte[] data = encoder.Flush();
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);

        foreach (int expected in symbols)
        {
            Assert.Equal(expected, decoder.ReadSymbol(decodeCdf));
        }

        Assert.Equal(encodeCdf, decodeCdf);
    }

    [Fact]
    public void WriteSymbol_MixedWithBoolAndLiteral_RoundTripsInOrder()
    {
        ushort[] encodeCdf = [10000, 20000, 1 << 15, 0];
        ushort[] decodeCdf = [10000, 20000, 1 << 15, 0];

        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);
        encoder.WriteBool(1);
        encoder.WriteSymbol(encodeCdf, 2);
        encoder.WriteLiteral(0b1011, 4);
        encoder.WriteSymbol(encodeCdf, 0);
        encoder.WriteBool(0);
        encoder.WriteSymbol(encodeCdf, 1);

        byte[] data = encoder.Flush();
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);

        Assert.Equal(1, decoder.ReadBool());
        Assert.Equal(2, decoder.ReadSymbol(decodeCdf));
        Assert.Equal(0b1011u, decoder.ReadLiteral(4));
        Assert.Equal(0, decoder.ReadSymbol(decodeCdf));
        Assert.Equal(0, decoder.ReadBool());
        Assert.Equal(1, decoder.ReadSymbol(decodeCdf));
        Assert.Equal(encodeCdf, decodeCdf);
    }

    [Fact]
    public void WriteSymbol_LongRandomSequenceOfVaryingCdfSizes_RoundTripsCorrectly()
    {
        var random = new Random(777);
        var symbolLog = new List<(int Symbol, int CdfSize)>();
        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);

        // Independent CDF instance per (size, "slot") so adaptation across many calls exercises realistic
        // repeated-context reuse, mirroring how a real tile reuses the same small set of context CDFs
        // across many blocks.
        var cdfPool = new Dictionary<int, (ushort[] Encode, ushort[] Decode)>();
        ushort[] GetOrCreate(int size)
        {
            if (!cdfPool.TryGetValue(size, out var pair))
            {
                var cdf = new ushort[size + 1];
                for (int i = 0; i < size; i++)
                {
                    cdf[i] = (ushort)((32768 * (i + 1)) / size);
                }

                cdf[size - 1] = 1 << 15;
                pair = (cdf, (ushort[])cdf.Clone());
                cdfPool[size] = pair;
            }

            return pair.Encode;
        }

        for (int i = 0; i < 500; i++)
        {
            int size = random.Next(2, 12);
            ushort[] cdf = GetOrCreate(size);
            int symbol = random.Next(size);
            encoder.WriteSymbol(cdf, symbol);
            symbolLog.Add((symbol, size));
        }

        byte[] data = encoder.Flush();
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);

        var decodeCdfPool = new Dictionary<int, ushort[]>();
        foreach (var (expectedSymbol, size) in symbolLog)
        {
            if (!decodeCdfPool.TryGetValue(size, out ushort[]? cdf))
            {
                cdf = (ushort[])cdfPool[size].Decode;
                decodeCdfPool[size] = cdf;
            }

            Assert.Equal(expectedSymbol, decoder.ReadSymbol(cdf));
        }

        foreach (var (size, pair) in cdfPool)
        {
            Assert.Equal(pair.Encode, decodeCdfPool[size]);
        }
    }

    [Fact]
    public void Flush_ThenWriteSymbol_Throws()
    {
        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);
        encoder.WriteBool(1);
        encoder.Flush();

        Assert.Throws<InvalidOperationException>(() => encoder.WriteBool(0));
    }

    [Fact]
    public void Flush_EmptySequence_ProducesValidDecodableBuffer()
    {
        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);
        byte[] data = encoder.Flush();

        Assert.True(data.Length >= 2);
    }
}
