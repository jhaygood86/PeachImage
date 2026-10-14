using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Entropy;
using PeachImage.Formats.Jxl.Entropy;
using ProductionWriter = PeachImage.Formats.Jxl.Encoder.Bitstream.JxlBitWriter;

namespace PeachImage.Tests.Formats.Jxl.Unit;

/// <summary>Encodes token streams with the rANS writer and decodes them with the existing entropy reader.</summary>
public class JxlEntropyEncoderTests
{
    private static JxlTokenList MakeTokens(int count, int numContexts, int seed, Func<Random, int, uint> value)
    {
        var random = new Random(seed);
        var tokens = new JxlTokenList();
        for (int i = 0; i < count; i++)
        {
            int context = random.Next(numContexts);
            tokens.Add(context, value(random, context));
        }

        return tokens;
    }

    private static void AssertRoundTrip(int numContexts, params JxlTokenList[] streams)
    {
        var plan = JxlEntropyPlan.Create(numContexts, streams);
        var writer = new ProductionWriter();
        plan.WriteHeader(writer);
        foreach (var stream in streams)
        {
            plan.WriteStream(writer, stream);
            writer.WriteBits(0x5A, 8); // a sentinel proves the stream consumed exactly its bits
        }

        var reader = new JxlBitReader(writer.ToArray());
        var code = JxlEntropyCode.Read(ref reader, numContexts, out byte[] contextMap);
        Assert.Equal(numContexts, contextMap.Length);
        foreach (var stream in streams)
        {
            using var symbols = new JxlSymbolReader(code, ref reader);
            foreach (var token in stream)
            {
                Assert.Equal(plan.ClusterOf(token.Context), contextMap[token.Context]);
                Assert.Equal(token.Value, symbols.ReadHybridUint(contextMap[token.Context], ref reader));
            }

            Assert.True(symbols.CheckFinalState());
            Assert.Equal(0x5Au, reader.ReadBits(8));
        }
    }

    [Fact]
    public void SingleContext_SingleSymbol() =>
        AssertRoundTrip(1, MakeTokens(1000, 1, 1, (_, _) => 7));

    [Fact]
    public void SingleContext_TwoSymbols() =>
        AssertRoundTrip(1, MakeTokens(5000, 1, 2, (r, _) => r.Next(10) == 0 ? 3u : 9u));

    [Fact]
    public void SingleContext_FewSymbols() =>
        AssertRoundTrip(1, MakeTokens(5000, 1, 3, (r, _) => (uint)r.Next(5)));

    [Fact]
    public void SingleContext_WideDistribution_UsesRunLengthCodedHistogram() =>
        AssertRoundTrip(1, MakeTokens(20000, 1, 4, (r, _) => (uint)Math.Min(r.Next(40), r.Next(40))));

    [Fact]
    public void SingleContext_LargeValues_CarryExtraBits() =>
        AssertRoundTrip(1, MakeTokens(20000, 1, 5, (r, _) => (uint)r.NextInt64(0, uint.MaxValue >> r.Next(1, 32))));

    [Fact]
    public void SingleContext_FullRangeValues() =>
        AssertRoundTrip(1, new JxlTokenList().With(0, 0u, 1u, 15u, 16u, 17u, 255u, 65535u, 1u << 20, int.MaxValue, uint.MaxValue, uint.MaxValue - 1));

    [Fact]
    public void SingleContext_SparseHistogramWithZeroRuns() =>
        AssertRoundTrip(1, MakeTokens(10000, 1, 6, (r, _) => r.Next(3) switch { 0 => 0u, 1 => 40u, _ => 120u }));

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(40)]
    public void MultipleContexts_ClusteredByDistribution(int numContexts) =>
        AssertRoundTrip(
            numContexts,
            MakeTokens(30000, numContexts, 7, (r, c) => (c & 1) == 0 ? (uint)r.Next(4) : (uint)(100 + r.Next(30))));

    [Fact]
    public void ManyContexts_WithDistinctDistributions_UsesCodedContextMap() =>
        AssertRoundTrip(
            60,
            MakeTokens(60000, 60, 8, (r, c) => (uint)(c % 12 * 20 + r.Next(1 + (c % 5)))));

    [Fact]
    public void UnusedContexts_AreTolerated() =>
        AssertRoundTrip(8, MakeTokens(500, 3, 9, (r, _) => (uint)r.Next(6)));

    [Fact]
    public void MultipleStreams_ShareOneCode() =>
        AssertRoundTrip(
            3,
            MakeTokens(3000, 3, 10, (r, c) => (uint)r.Next(8 + c)),
            MakeTokens(7000, 3, 11, (r, c) => (uint)r.Next(8 + c)),
            new JxlTokenList());

    [Fact]
    public void EmptyInput_WritesDecodableCode() =>
        AssertRoundTrip(1, new JxlTokenList());
}

internal static class JxlTokenListExtensions
{
    public static JxlTokenList With(this JxlTokenList list, int context, params uint[] values)
    {
        foreach (uint value in values)
        {
            list.Add(context, value);
        }

        return list;
    }
}
