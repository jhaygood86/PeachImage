namespace PeachImage.Internal.Icc;

/// <summary>
/// The RGB-matrix-TRC device↔PCS transform (three tone curves plus a fixed RGB→XYZ matrix) — never resolved
/// to for a CMYK profile (this transform kind is only valid for a 3-channel RGB device space), but ported for
/// completeness of <see cref="IccProfile"/>'s transform-selection precedence. Ported from Wacton/Unicolour's
/// <c>Icc/TransformTrcMatrix.cs</c> (MIT license) — see THIRD-PARTY-LICENSES.md.
/// </summary>
internal sealed class IccTransformTrcMatrix : IccTransform
{
    private static readonly IccCurve[] IdentityMCurves = [IccTableCurve.Identity, IccTableCurve.Identity, IccTableCurve.Identity];

    private readonly Lazy<IccCurve[]> bCurves;
    private readonly Lazy<IccCurve[]> bCurvesInverse;
    private readonly Lazy<IccMatrices> matrices;
    private readonly Lazy<IccMatrices> matricesInverse;

    internal IccTransformTrcMatrix(IccHeader header, IccTags tags)
        : base(header, tags, hasPerceptualHandling: false)
    {
        bCurves = new Lazy<IccCurve[]>(() => [tags.RedTrc.Value!, tags.GreenTrc.Value!, tags.BlueTrc.Value!]);
        matrices = new Lazy<IccMatrices>(() => new IccMatrices(GetMatrix(), IccMatrices.ZeroOffset));

        bCurvesInverse = new Lazy<IccCurve[]>(() => [tags.RedTrc.Value!.Inverse(), tags.GreenTrc.Value!.Inverse(), tags.BlueTrc.Value!.Inverse()]);
        matricesInverse = new Lazy<IccMatrices>(() => new IccMatrices(GetMatrix().Inverse(), IccMatrices.ZeroOffset));
    }

    internal override IccVector3 ToXyz(ReadOnlySpan<double> deviceValues, IccIntent intent)
    {
        // This transform can only be used with an XYZ PCS -- no need to handle a Lab PCS.
        Span<double> xyz = stackalloc double[3];
        Bm(deviceValues, bCurves.Value, matrices.Value, IdentityMCurves, xyz);
        return AdjustXyz(new IccVector3(xyz[0], xyz[1], xyz[2]), intent, isDeviceToPcs: true);
    }

    internal override void FromXyz(IccVector3 xyz, IccIntent intent, Span<double> deviceValues)
    {
        xyz = AdjustXyz(xyz, intent, isDeviceToPcs: false);
        Span<double> pcsValues = [xyz.X, xyz.Y, xyz.Z];
        Mb(pcsValues, IdentityMCurves, matricesInverse.Value, bCurvesInverse.Value, deviceValues);
    }

    /// <summary>
    /// The PCS-to-device direction as plain data -- a 3x3 matrix from D50 XYZ to linear device RGB followed by one 2048-entry
    /// inverse-curve table per channel -- for callers that convert many pixels. Only valid for intents that leave the PCS
    /// untouched (<see cref="IccIntent.RelativeColorimetric"/>).
    /// </summary>
    internal bool TryGetFromXyz(IccIntent intent, out IccMatrix3x3 matrix, out double[][] inverseTables)
    {
        matrix = default;
        inverseTables = [];
        if (intent != IccIntent.RelativeColorimetric)
        {
            return false;
        }

        matrix = matricesInverse.Value.Multiply;
        var curves = bCurvesInverse.Value;
        inverseTables = new double[curves.Length][];
        for (int i = 0; i < curves.Length; i++)
        {
            if (curves[i] is not IccTableCurve table || table.Table.Length < 2)
            {
                return false;
            }

            inverseTables[i] = table.Table;
        }

        return true;
    }

    private IccMatrix3x3 GetMatrix()
    {
        var red = Tags.RedMatrixColumn.Value!;
        var green = Tags.GreenMatrixColumn.Value!;
        var blue = Tags.BlueMatrixColumn.Value!;

        return new IccMatrix3x3(
            red.X, green.X, blue.X,
            red.Y, green.Y, blue.Y,
            red.Z, green.Z, blue.Z);
    }
}
