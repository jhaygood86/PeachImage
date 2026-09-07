using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Correctness gate for <see cref="Av1CdfContext.CopyFrom"/> -- the scratch-refresh primitive
/// <c>Av1TileEncoder.TileState.ScratchCdf</c> relies on being reseeded from the tile's real, live CDF
/// state once per RD candidate (see that field's own remarks). A subtle bug here (e.g. only copying
/// <c>n</c> probability slots and leaving the adaptation count reset to its default) would silently bias
/// every candidate's own simulated adaptation rate without producing an obviously wrong result --
/// exactly the failure mode worth a direct, targeted test for, not just relying on end-to-end encode
/// output looking plausible.
/// </summary>
public class Av1CdfContextTests
{
    [Fact]
    public void CopyFrom_CopiesAdaptedCoefficientTableValues()
    {
        var real = new Av1CdfContext(baseQIdx: 0);
        var scratch = new Av1CdfContext(baseQIdx: 0);

        // Adapt the real context's TxbSkip[0][0] table away from its shipped default a few times, so
        // CopyFrom has something non-default to actually prove it copied.
        for (int i = 0; i < 5; i++)
        {
            Av1CdfAdaptation.AdaptCdf(real.TxbSkip[0][0], real.TxbSkip[0][0].Length - 1, symbol: 1);
        }

        Assert.NotEqual(scratch.TxbSkip[0][0], real.TxbSkip[0][0]);

        scratch.CopyFrom(real);

        Assert.Equal(real.TxbSkip[0][0], scratch.TxbSkip[0][0]);
    }

    /// <summary>
    /// The adaptation-count trailing element (<c>cdf[n]</c>, spec §8.2.6) drives <see cref="Av1CdfAdaptation.AdaptCdf"/>'s
    /// own rate formula -- a scratch copy that only copies probability slots and leaves this at its
    /// freshly-constructed 0 would make every simulated candidate adapt at the fastest possible rate
    /// regardless of how much real history the tile actually has, a wrong-in-a-plausible-looking-way bug
    /// (see <see cref="Av1TileEncoder"/>'s own <c>ScratchCdf</c> remarks). This test fails if that
    /// specific mistake is reintroduced.
    /// </summary>
    [Fact]
    public void CopyFrom_CopiesAdaptationCountNotJustProbabilities()
    {
        var real = new Av1CdfContext(baseQIdx: 0);
        var scratch = new Av1CdfContext(baseQIdx: 0);

        for (int i = 0; i < 20; i++)
        {
            Av1CdfAdaptation.AdaptCdf(real.CoeffBase[0][0][0], real.CoeffBase[0][0][0].Length - 1, symbol: 0);
        }

        int realCount = real.CoeffBase[0][0][0][^1];
        Assert.True(realCount > 0, "Test setup should have advanced the real context's adaptation count above its default 0.");

        scratch.CopyFrom(real);

        Assert.Equal(realCount, scratch.CoeffBase[0][0][0][^1]);
    }

    [Fact]
    public void CopyFrom_NeverReassignsArrayReferences()
    {
        var real = new Av1CdfContext(baseQIdx: 0);
        var scratch = new Av1CdfContext(baseQIdx: 0);
        ushort[] originalArrayReference = scratch.TxbSkip[0][0];

        scratch.CopyFrom(real);

        Assert.Same(originalArrayReference, scratch.TxbSkip[0][0]);
    }

    [Fact]
    public void CopyFrom_LeavesUnrelatedTablesUntouched()
    {
        var real = new Av1CdfContext(baseQIdx: 0);
        var scratch = new Av1CdfContext(baseQIdx: 0);

        // Mutate a non-coefficient table (partition CDFs) on the real context -- CopyFrom is documented to
        // only touch the coefficient-related tables WriteCoeffs reads, so this must NOT propagate.
        Av1CdfAdaptation.AdaptCdf(real.PartitionW32[0], real.PartitionW32[0].Length - 1, symbol: 3);
        Assert.NotEqual(scratch.PartitionW32[0], real.PartitionW32[0]);

        scratch.CopyFrom(real);

        Assert.NotEqual(scratch.PartitionW32[0], real.PartitionW32[0]);
    }
}
