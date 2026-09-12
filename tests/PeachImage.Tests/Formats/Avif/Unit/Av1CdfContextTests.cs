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

    /// <summary>
    /// <see cref="Av1CdfContext.CopyFrom"/> copies every table, not just the coefficient-related ones --
    /// see its own remarks for the real bug this corrected (a stale claim that "nothing outside coefficient
    /// coding ever reads a context copied this way", contradicted by <c>Av1TileEncoder</c>'s own real
    /// decision-phase cost estimators reading <c>TileState.CostCdf</c>'s partition/uv_mode/angle_delta/
    /// palette/mv tables extensively). Covers one table from each of the previously-uncopied groups
    /// (partition, uv_mode, palette, mv) as a representative sample, not an exhaustive per-field sweep.
    /// </summary>
    [Fact]
    public void CopyFrom_CopiesNonCoefficientTablesToo()
    {
        var real = new Av1CdfContext(baseQIdx: 0);
        var scratch = new Av1CdfContext(baseQIdx: 0);

        Av1CdfAdaptation.AdaptCdf(real.PartitionW32[0], real.PartitionW32[0].Length - 1, symbol: 3);
        Av1CdfAdaptation.AdaptCdf(real.UvModeCflAllowed[0], real.UvModeCflAllowed[0].Length - 1, symbol: 5);
        Av1CdfAdaptation.AdaptCdf(real.PaletteYSize[0], real.PaletteYSize[0].Length - 1, symbol: 2);
        Av1CdfAdaptation.AdaptCdf(real.MvJoint, real.MvJoint.Length - 1, symbol: 1);

        Assert.NotEqual(scratch.PartitionW32[0], real.PartitionW32[0]);
        Assert.NotEqual(scratch.UvModeCflAllowed[0], real.UvModeCflAllowed[0]);
        Assert.NotEqual(scratch.PaletteYSize[0], real.PaletteYSize[0]);
        Assert.NotEqual(scratch.MvJoint, real.MvJoint);

        scratch.CopyFrom(real);

        Assert.Equal(real.PartitionW32[0], scratch.PartitionW32[0]);
        Assert.Equal(real.UvModeCflAllowed[0], scratch.UvModeCflAllowed[0]);
        Assert.Equal(real.PaletteYSize[0], scratch.PaletteYSize[0]);
        Assert.Equal(real.MvJoint, scratch.MvJoint);
    }
}
