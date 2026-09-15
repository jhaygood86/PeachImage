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
