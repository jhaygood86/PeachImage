namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// Per-plane loop restoration unit info (spec's frame-sized <c>LrType</c>/<c>LrWiener</c>/<c>LrSgrSet</c>/
/// <c>LrSgrXqd</c> arrays), populated during tile decode by <c>read_lr_unit()</c> and consumed later by
/// the loop restoration filter pass (spec §7.17). One instance per plane, shared across every tile of the
/// frame the same way the reconstructed planes are.
/// </summary>
internal sealed class Av1RestorationUnitGrid
{
    public required int UnitRows { get; init; }

    public required int UnitCols { get; init; }

    public required int UnitSize { get; init; }

    /// <summary>Flat <c>[unitRow * UnitCols + unitCol]</c>, one of <see cref="Av1LoopRestorationParams"/>'s <c>Restore*</c> constants.</summary>
    public required int[] LrType { get; init; }

    /// <summary>Flat <c>[((unitRow * UnitCols + unitCol) * 2 + pass) * 3 + j]</c> -- 3 coefficients per pass, 2 passes per unit.</summary>
    public required int[] LrWiener { get; init; }

    public required int[] LrSgrSet { get; init; }

    /// <summary>Flat <c>[(unitRow * UnitCols + unitCol) * 2 + i]</c> -- 2 entries per unit.</summary>
    public required int[] LrSgrXqd { get; init; }

    /// <summary><c>count_units_in_frame(unitSize, frameSize)</c> (spec §5.11.57).</summary>
    public static int CountUnitsInFrame(int unitSize, int frameSize) => Math.Max((frameSize + (unitSize >> 1)) / unitSize, 1);

    public static Av1RestorationUnitGrid Create(int unitSize, int frameSizeForRows, int frameSizeForCols)
    {
        int unitRows = CountUnitsInFrame(unitSize, frameSizeForRows);
        int unitCols = CountUnitsInFrame(unitSize, frameSizeForCols);
        int count = unitRows * unitCols;

        return new Av1RestorationUnitGrid
        {
            UnitRows = unitRows,
            UnitCols = unitCols,
            UnitSize = unitSize,
            LrType = new int[count],
            LrWiener = new int[count * 2 * 3],
            LrSgrSet = new int[count],
            LrSgrXqd = new int[count * 2],
        };
    }
}
