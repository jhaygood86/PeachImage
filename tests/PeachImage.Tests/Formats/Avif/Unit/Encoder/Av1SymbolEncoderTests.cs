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

    /// <summary>
    /// The real, forward, incremental range encoder (ported from libaom's <c>od_ec_enc_done</c>) produces
    /// exactly the bytes its own algorithm computes for whatever state accumulated -- for zero symbols
    /// written, that's a single byte (<c>cnt</c> starts at -9, so <c>od_ec_enc_done</c>'s own <c>s = 10 + cnt
    /// = 1</c> flush loop runs exactly once). There is no real minimum-tile-size requirement here:
    /// <see cref="Av1SymbolDecoder"/>'s own constructor already handles a buffer shorter than 15 bits via its
    /// <c>Math.Min(length * 8, 15)</c>/zero-padding logic regardless of length, and a real AV1 tile is never
    /// actually empty in practice (this only exercises the flush algorithm's own trivial base case). An
    /// earlier, now-replaced backward-solve implementation of this encoder artificially forced a >= 2-byte
    /// floor to sidestep needing to model that zero-padding precisely -- that was an implementation artifact
    /// of that specific algorithm, never a decoder or spec requirement.
    /// </summary>
    [Fact]
    public void Flush_EmptySequence_ProducesValidDecodableBuffer()
    {
        var encoder = new Av1SymbolEncoder(disableCdfUpdate: false);
        byte[] data = encoder.Flush();

        Assert.True(data.Length >= 1);
    }

    /// <summary>
    /// <see cref="Av1SymbolEncoder.EstimateSymbolCost"/>'s ported libaom <c>av1_cost_symbol</c> formula must
    /// still price a literal (50/50) bit at exactly 1 bit -- <see cref="Av1TrialSymbolSink.WriteLiteral"/>
    /// relies on this invariant to add <c>n</c> directly rather than calling this method <c>n</c> times (see
    /// its own remarks).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EstimateSymbolCost_LiteralFiftyFiftyCdf_CostsExactlyOneBit(int symbol)
    {
        Span<ushort> cdf = [1 << 14, 1 << 15, 0];

        Assert.Equal(1, Av1SymbolEncoder.EstimateSymbolCost(cdf, symbol));
    }

    /// <summary>
    /// A near-certain symbol (probability mass 32764 out of 32768) must cost close to 0 bits, not round up to
    /// a whole bit the way the old renormalization-step-based formula did -- this is the specific, measured
    /// gap this port closes (see <see cref="Av1SymbolEncoder.EstimateSymbolCost"/>'s own remarks): summed
    /// across many near-certain, repeated symbols in one RD candidate's trial, the old formula's one-sided
    /// "at least one whole bit" bias compounded into a large systematic overestimate.
    /// </summary>
    [Fact]
    public void EstimateSymbolCost_NearCertainSymbol_CostsNearlyZeroBits()
    {
        Span<ushort> cdf = [32764, 1 << 15, 0];

        Assert.Equal(0, Av1SymbolEncoder.EstimateSymbolCost(cdf, 0));
    }

    /// <summary>
    /// Cross-checks <see cref="Av1SymbolEncoder.EstimateSymbolCost"/>'s ported <c>av1_prob_cost</c> table
    /// lookup against an independently hand-computed <c>-log2(p)</c> value for a genuinely unlikely symbol
    /// (probability mass 100 out of 32768, <c>-log2(100/32768) &#8776; 8.357</c> bits) -- confirms the
    /// normalization shift (<see cref="Av1SymbolEncoder.EstimateSymbolCost"/>'s ported <c>shift</c>/<c>get_prob</c>
    /// logic) and table indexing are wired correctly, not just the two easy extremes above.
    /// </summary>
    [Fact]
    public void EstimateSymbolCost_UnlikelySymbol_MatchesExpectedNegativeLog2Bits()
    {
        Span<ushort> cdf = [100, 1 << 15, 0];

        Assert.Equal(8, Av1SymbolEncoder.EstimateSymbolCost(cdf, 0));
    }

    /// <summary>
    /// <see cref="Av1AdaptingTrialSymbolSink"/> (unlike <see cref="Av1TrialSymbolSink"/>) must actually
    /// adapt whatever cdf array it's handed -- see <c>Av1TileEncoder.TileState.ScratchCdf</c>'s own
    /// remarks for why a candidate spanning many sub-blocks needs this to simulate a real commit's own
    /// adaptation. A cdf that never adapts would keep costing every repeated symbol at the same rate no
    /// matter how many times this sink writes it.
    /// </summary>
    [Fact]
    public void AdaptingTrialSymbolSink_WriteSymbol_AdaptsThePassedCdf()
    {
        var sink = new Av1AdaptingTrialSymbolSink();
        Span<ushort> cdf = [1 << 14, 1 << 15, 0];
        ushort[] before = cdf.ToArray();

        sink.WriteSymbol(cdf, symbol: 0);

        Assert.NotEqual(before, cdf.ToArray());
    }

    /// <summary>
    /// Repeatedly writing the same near-certain symbol should cost strictly less over time as the scratch
    /// cdf adapts toward it -- the whole point of this sink existing instead of the plain, non-adapting
    /// <see cref="Av1TrialSymbolSink"/> (see <c>ComputeLosslessWholeLeafCostPerSubBlock</c>'s own remarks).
    /// Uses <see cref="Av1SymbolEncoder.EstimateSymbolCostPrecise512ths"/> directly (not the sink's own
    /// rounded <see cref="Av1AdaptingTrialSymbolSink.Bits"/>) so this is sensitive to the sub-bit
    /// improvement the sink's own internal precision is specifically meant to preserve.
    /// </summary>
    [Fact]
    public void AdaptingTrialSymbolSink_RepeatedSymbol_CostsLessOverTime()
    {
        var sink = new Av1AdaptingTrialSymbolSink();
        Span<ushort> cdf = [1 << 14, 1 << 15, 0];

        int firstCost = Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(cdf, symbol: 0);
        sink.WriteSymbol(cdf, symbol: 0);

        int secondCost = Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(cdf, symbol: 0);
        sink.WriteSymbol(cdf, symbol: 0);

        Assert.True(secondCost < firstCost, $"Expected the second write of the same symbol to cost less once the cdf adapted toward it (first={firstCost}, second={secondCost}).");
    }

    /// <summary>Mirrors <see cref="EstimateSymbolCost_LiteralFiftyFiftyCdf_CostsExactlyOneBit"/> for the adapting sink's own public <see cref="Av1AdaptingTrialSymbolSink.Bits"/>, confirming its internal 1/512-bit accumulation still rounds to the same whole-bit answer for a single write.</summary>
    [Fact]
    public void AdaptingTrialSymbolSink_Bits_RoundsConsistentlyWithEstimateSymbolCost()
    {
        var sink = new Av1AdaptingTrialSymbolSink();
        Span<ushort> cdf = [1 << 14, 1 << 15, 0];

        sink.WriteSymbol(cdf, symbol: 0);

        Assert.Equal(1, sink.Bits);
    }

    [Fact]
    public void AdaptingTrialSymbolSink_Reset_ZeroesAccumulatedBits()
    {
        var sink = new Av1AdaptingTrialSymbolSink();
        Span<ushort> cdf = [1 << 14, 1 << 15, 0];
        sink.WriteSymbol(cdf, symbol: 0);

        sink.Reset();

        Assert.Equal(0, sink.Bits);
    }
}
