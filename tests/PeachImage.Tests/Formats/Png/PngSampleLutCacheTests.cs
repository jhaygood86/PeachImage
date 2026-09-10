using PeachImage.Formats.Png.Internal;
using Xunit;

namespace PeachImage.Tests.Formats.Png;

/// <summary>
/// The bit-depth-16 sample table is built once per gamma exponent, not once per decode.
/// </summary>
/// <remarks>
/// <para>
/// At depth 16 the table is <c>ushort[65536]</c> — 128 KB, over the 85 KB large-object threshold —
/// so a fresh one per decode went straight onto the Large Object Heap, reclaimable only by a gen2
/// collection. Sampled allocation over a 26-document PDF corpus measured 731 MB in this method
/// alone, a quarter of all large-object traffic there.
/// </para>
/// <para>
/// Every test here shares one process-wide cache with the rest of the suite, so the class clears it
/// first: xUnit runs a class's own tests one at a time, and nothing outside this class adds to the
/// cache (only a 16-bit decode with <c>ScreenGamma</c> set does, and no other test sets it), which
/// together make the counts and the reference identities below deterministic.
/// </para>
/// </remarks>
public class PngSampleLutCacheTests
{
    public PngSampleLutCacheTests() => PngSampleLut.ClearCorrectedTables();

    [Fact]
    public void Depth16_ReturnsTheSameTableForTheSameGamma()
    {
        var first = PngSampleLut.Build(16, null, null);
        var second = PngSampleLut.Build(16, null, null);

        // By reference, deliberately. Two independently built tables are equal element for element,
        // so Assert.Equal would pass against uncached code and prove nothing at all.
        Assert.Same(first, second);
    }

    [Fact]
    public void Depth16_ReturnsADifferentTableForADifferentGamma()
    {
        var plain = PngSampleLut.Build(16, null, null);
        var corrected = PngSampleLut.Build(16, 1.0 / 2.2, 2.2);

        Assert.NotSame(plain, corrected);
        Assert.NotEqual(plain, corrected);
    }

    [Fact]
    public void Depth16_DoesNotSpendACacheSlotOnAFileGammaThatCorrectsNothing()
    {
        // A file gamma with no screen gamma to correct against leaves every sample unchanged, so all
        // three of these are the identity table. Keyed on the raw (fileGamma, screenGamma) pair they
        // were three entries holding byte-identical content — the shape that let a corpus of ordinary
        // gamma-bearing files fill the cap without one entry that saves any work.
        var none = PngSampleLut.Build(16, null, null);
        var srgb = PngSampleLut.Build(16, 1.0 / 2.2, null);
        var unusual = PngSampleLut.Build(16, 1.0 / 1.0, null);

        Assert.Same(none, srgb);
        Assert.Same(none, unusual);
        Assert.Equal(0, PngSampleLut.CachedCorrectedTableCount);
    }

    [Fact]
    public void Depth16_SharesOneTableAcrossGammaPairsWithTheSameExponent()
    {
        // The exponent is fileGamma / screenGamma, and it alone decides the contents: 0.5/1.0 and
        // 1.1/2.2 are the same table.
        var first = PngSampleLut.Build(16, 0.5, 1.0);
        var second = PngSampleLut.Build(16, 1.1, 2.2);

        Assert.Same(first, second);
        Assert.Equal(1, PngSampleLut.CachedCorrectedTableCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void EveryDepthStillProducesTheTableItAlwaysDid(int bitDepth)
    {
        var inputMax = (1 << bitDepth) - 1;
        var outputMax = bitDepth <= 8 ? 255 : 65535;

        var lut = PngSampleLut.Build(bitDepth, null, null);

        Assert.Equal(inputMax + 1, lut.Length);
        Assert.Equal(0, lut[0]);
        Assert.Equal(outputMax, lut[inputMax]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void GammaCorrectionStillProducesTheTableItAlwaysDid(int bitDepth)
    {
        var inputMax = (1 << bitDepth) - 1;
        var outputMax = bitDepth <= 8 ? 255 : 65535;
        var exponent = 1.0 / 2.2 / 2.2;

        // Against PngDecoderOptions.ScreenGamma's stated contract,
        // output = round(max * (input / max) ^ (fileGamma / screenGamma)) — not against a value read
        // back off the table, and in the direction that catches an inverted exponent.
        var lut = PngSampleLut.Build(bitDepth, 1.0 / 2.2, 2.2);

        for (var i = 0; i <= inputMax; i++)
        {
            var expected = (ushort)Math.Clamp(
                Math.Round(Math.Pow((double)i / inputMax, exponent) * outputMax, MidpointRounding.AwayFromZero),
                0,
                outputMax);

            Assert.Equal(expected, lut[i]);
        }
    }

    [Fact]
    public void ACachedTableIsNotObservablySharedMutableState()
    {
        // The table is handed out by reference and consumed as ReadOnlySpan<ushort> by
        // PngRowResolver, so nothing writes to it. This pins the contract that makes sharing safe:
        // a caller that did mutate it would corrupt every later decode, and this test would be the
        // thing that noticed.
        var lut = PngSampleLut.Build(16, null, null);
        var sentinel = lut[1234];

        _ = PngSampleLut.Build(16, null, null);

        Assert.Equal(sentinel, PngSampleLut.Build(16, null, null)[1234]);
    }

    [Fact]
    public void TheCacheStopsGrowingPastItsCap()
    {
        for (var i = 1; i <= PngSampleLut.MaxEntries; i++)
        {
            _ = PngSampleLut.Build(16, 1.0 / i, 2.2);
        }

        Assert.Equal(PngSampleLut.MaxEntries, PngSampleLut.CachedCorrectedTableCount);

        // gAMA is attacker-controlled, so past the cap a new exponent must build and return without
        // being kept: same inputs, a fresh table every time, and the cache no larger than before.
        var overflow = 1.0 / (PngSampleLut.MaxEntries + 1);
        var first = PngSampleLut.Build(16, overflow, 2.2);
        var second = PngSampleLut.Build(16, overflow, 2.2);

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        Assert.Equal(PngSampleLut.MaxEntries, PngSampleLut.CachedCorrectedTableCount);
    }

    [Fact]
    public void ConcurrentMissesCannotGrowTheCachePastItsCap()
    {
        // The motivating case is a server decoding many images at once, which is also what defeats a
        // non-atomic "count, then add": threads that finish building within the same window all read
        // a count below the cap and all add. Dedicated threads released together by a barrier are
        // what makes that visible (a Parallel.ForEach does not — the pool injects threads slowly
        // enough that the first adds land before the rest arrive), and the rounds are there because
        // one round catches it only sometimes: measured against the unsynchronized version, a single
        // round of 64 exceeded the cap in 1 run of 5, eight rounds in 5 of 5.
        const int rounds = 8;
        const int threads = 64;

        for (var round = 0; round < rounds; round++)
        {
            PngSampleLut.ClearCorrectedTables();

            var barrier = new Barrier(threads);
            var workers = Enumerable.Range(1, threads)
                .Select(i => new Thread(() =>
                {
                    // A distinct exponent per thread per round, so every one of them is a miss.
                    barrier.SignalAndWait();
                    PngSampleLut.Build(16, 1.0 / (i + (round * threads)), 1.0);
                }))
                .ToArray();

            foreach (var worker in workers)
            {
                worker.Start();
            }

            foreach (var worker in workers)
            {
                worker.Join();
            }

            Assert.Equal(PngSampleLut.MaxEntries, PngSampleLut.CachedCorrectedTableCount);
        }
    }
}
