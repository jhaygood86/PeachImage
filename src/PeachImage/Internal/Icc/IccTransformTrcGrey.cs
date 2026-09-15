namespace PeachImage.Internal.Icc;

/// <summary>
/// The single-channel grey-TRC device↔PCS transform — never resolved to for a CMYK profile, but ported for
/// completeness of <see cref="IccProfile"/>'s transform-selection precedence. Ported from Wacton/Unicolour's
/// <c>Icc/TransformTrcGrey.cs</c> (MIT license) — see THIRD-PARTY-LICENSES.md.
/// </summary>
internal sealed class IccTransformTrcGrey : IccTransform
{
    // When using a Lab PCS, the grey TRC is applied to the ICC-normalized ("IccLab") representation.
    private static readonly IccVector3 RefWhiteIccLab = LabToIccLab(XyzToLab(RefWhite));

    private readonly Lazy<IccCurve[]> bCurves;
    private readonly Lazy<IccCurve[]> bCurvesInverse;

    internal IccTransformTrcGrey(IccHeader header, IccTags tags)
        : base(header, tags, hasPerceptualHandling: false)
    {
        bCurves = new Lazy<IccCurve[]>(() => [tags.GreyTrc.Value!]);
        bCurvesInverse = new Lazy<IccCurve[]>(() => [tags.GreyTrc.Value!.Inverse()]);
    }

    internal override IccVector3 ToXyz(ReadOnlySpan<double> deviceValues, IccIntent intent)
    {
        Span<double> pcsValues = stackalloc double[1];
        B(deviceValues, bCurves.Value, pcsValues);
        double grey = pcsValues[0];

        IccVector3 xyz = IsLabPcs
            ? LabToXyz(IccLabToLab(new IccVector3(RefWhiteIccLab.X * grey, RefWhiteIccLab.Y * grey, RefWhiteIccLab.Z * grey)))
            : new IccVector3(RefWhite.X * grey, RefWhite.Y * grey, RefWhite.Z * grey);

        return AdjustXyz(xyz, intent, isDeviceToPcs: true);
    }

    internal override void FromXyz(IccVector3 xyz, IccIntent intent, Span<double> deviceValues)
    {
        xyz = AdjustXyz(xyz, intent, isDeviceToPcs: false);

        double grey;
        if (IsLabPcs)
        {
            var iccLab = LabToIccLab(XyzToLab(xyz));
            grey = iccLab.X / RefWhiteIccLab.X; // L is the lightness.
        }
        else
        {
            grey = xyz.Y / RefWhite.Y; // Y is the luminance.
        }

        Span<double> pcsValues = [grey];
        B(pcsValues, bCurvesInverse.Value, deviceValues);
    }
}
