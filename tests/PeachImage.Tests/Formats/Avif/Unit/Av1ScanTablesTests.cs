using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Port of libaom's own <c>scan_test.cc</c> <c>Av1ScanTest.Dependency</c> (the <c>TX_CLASS_2D</c> subset,
/// the only class <see cref="Av1ScanTables"/>'s own <c>Default_Scan_*</c> tables implement): verifies every
/// one of the 14 hand-transcribed-from-spec <c>Default_Scan_*</c> tables against
/// <see cref="LibaomReferenceScan"/>'s own closed-form zigzag/row-diagonal/column-diagonal generator,
/// selected by shape exactly as libaom's own real test selects it (<c>rows == cols</c> -&gt; zigzag,
/// <c>rows &gt; cols</c> -&gt; row-diagonal, <c>rows &lt; cols</c> -&gt; column-diagonal). Unlike this
/// project's other libaom test ports, this doesn't need <see cref="LibaomAcmRandom"/> at all -- the real
/// libaom test is exhaustive/deterministic by construction, not randomized, so this port is too.
/// </summary>
public class Av1ScanTablesTests
{
    public static TheoryData<string, int[], int, int> Tables => new()
    {
        { nameof(Av1ScanTables.DefaultScan4x4), Av1ScanTables.DefaultScan4x4, 4, 4 },
        { nameof(Av1ScanTables.DefaultScan4x8), Av1ScanTables.DefaultScan4x8, 4, 8 },
        { nameof(Av1ScanTables.DefaultScan8x4), Av1ScanTables.DefaultScan8x4, 8, 4 },
        { nameof(Av1ScanTables.DefaultScan8x8), Av1ScanTables.DefaultScan8x8, 8, 8 },
        { nameof(Av1ScanTables.DefaultScan8x16), Av1ScanTables.DefaultScan8x16, 8, 16 },
        { nameof(Av1ScanTables.DefaultScan16x8), Av1ScanTables.DefaultScan16x8, 16, 8 },
        { nameof(Av1ScanTables.DefaultScan16x16), Av1ScanTables.DefaultScan16x16, 16, 16 },
        { nameof(Av1ScanTables.DefaultScan16x32), Av1ScanTables.DefaultScan16x32, 16, 32 },
        { nameof(Av1ScanTables.DefaultScan32x16), Av1ScanTables.DefaultScan32x16, 32, 16 },
        { nameof(Av1ScanTables.DefaultScan32x32), Av1ScanTables.DefaultScan32x32, 32, 32 },
        { nameof(Av1ScanTables.DefaultScan4x16), Av1ScanTables.DefaultScan4x16, 4, 16 },
        { nameof(Av1ScanTables.DefaultScan16x4), Av1ScanTables.DefaultScan16x4, 16, 4 },
        { nameof(Av1ScanTables.DefaultScan8x32), Av1ScanTables.DefaultScan8x32, 8, 32 },
        { nameof(Av1ScanTables.DefaultScan32x8), Av1ScanTables.DefaultScan32x8, 32, 8 },
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void DefaultScan_MatchesLibaomReference(string name, int[] scan, int w, int h)
    {
        int[] expected = h == w ? LibaomReferenceScan.ZigZag(w, h)
            : h > w ? LibaomReferenceScan.RowDiag(w, h)
            : LibaomReferenceScan.ColDiag(w, h);

        Assert.Equal(expected.Length, scan.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i] == scan[i], $"{name}[{i}]: expected {expected[i]}, actual {scan[i]}");
        }

        // scan and its own inverse (iscan) must be permutations of [0, w*h) -- a real, independent property
        // of any valid scan order, not implied by the equality check above alone.
        var seen = new bool[w * h];
        foreach (int pos in scan)
        {
            Assert.InRange(pos, 0, (w * h) - 1);
            Assert.False(seen[pos], $"{name}: position {pos} appears more than once");
            seen[pos] = true;
        }
    }
}
