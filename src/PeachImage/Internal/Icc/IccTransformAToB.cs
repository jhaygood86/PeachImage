namespace PeachImage.Internal.Icc;

/// <summary>
/// The LUT-based device↔PCS transform (<c>AToB0/1/2</c>/<c>BToA0/1/2</c> tags) — the only transform kind a
/// real CMYK ICC profile ever resolves to, since matrix/TRC-only profiles are only valid for RGB/Gray device
/// spaces. Ported from Wacton/Unicolour's <c>Icc/TransformAToB.cs</c> (MIT license) — see
/// THIRD-PARTY-LICENSES.md.
/// </summary>
internal sealed class IccTransformAToB : IccTransform
{
    internal IccTransformAToB(IccHeader header, IccTags tags)
        : base(header, tags, hasPerceptualHandling: true)
    {
    }

    internal override IccVector3 ToXyz(ReadOnlySpan<double> deviceValues, IccIntent intent)
    {
        var luts = SelectLuts(intent, forward: true);

        Span<double> pcsValues = stackalloc double[3];
        ApplyLuts(deviceValues, luts, pcsValues);

        IccVector3 xyz;
        if (IsLabPcs)
        {
            var iccLab = new IccVector3(pcsValues[0], pcsValues[1], pcsValues[2]);
            iccLab = luts.Type == IccLutType.Lut16 ? IccLab2ToIccLab4(iccLab) : iccLab;
            xyz = LabToXyz(IccLabToLab(iccLab));
        }
        else
        {
            var iccXyz = new IccVector3(pcsValues[0], pcsValues[1], pcsValues[2]);
            xyz = IccXyzToXyz(iccXyz);
        }

        return AdjustXyz(xyz, intent, isDeviceToPcs: true);
    }

    internal override void FromXyz(IccVector3 xyz, IccIntent intent, Span<double> deviceValues)
    {
        // Unlike TRC transforms, which are trivially reversible, an AToB transform needs its own explicit
        // BToA tag for the reverse direction, which isn't guaranteed to be present (e.g. an input "scnr"
        // profile only ever needs scanner-device -> PCS, so has little reason to define the reverse).
        if (!Tags.Has(IccSignatures.BToA0))
        {
            throw new NotSupportedException("This ICC profile has no BToA transform defined (device values cannot be derived from PCS values).");
        }

        var luts = SelectLuts(intent, forward: false);

        xyz = AdjustXyz(xyz, intent, isDeviceToPcs: false);

        Span<double> pcsValues = stackalloc double[3];
        if (IsLabPcs)
        {
            var iccLab = LabToIccLab(XyzToLab(xyz));
            iccLab = luts.Type == IccLutType.Lut16 ? IccLab4ToIccLab2(iccLab) : iccLab;
            pcsValues[0] = iccLab.X;
            pcsValues[1] = iccLab.Y;
            pcsValues[2] = iccLab.Z;
        }
        else
        {
            var iccXyz = XyzToIccXyz(xyz);
            pcsValues[0] = iccXyz.X;
            pcsValues[1] = iccXyz.Y;
            pcsValues[2] = iccXyz.Z;
        }

        ApplyLuts(pcsValues, luts, deviceValues);
    }

    private IccLuts SelectLuts(IccIntent intent, bool forward)
    {
        // AToB0/BToA0 (perceptual) is used as the fallback regardless of intent whenever AToB1/AToB2 (or
        // their BToA counterparts) is missing.
        if (forward)
        {
            return intent switch
            {
                IccIntent.Perceptual => Tags.AToB0.Value!,
                IccIntent.RelativeColorimetric => Tags.Has(IccSignatures.AToB1) ? Tags.AToB1.Value! : Tags.AToB0.Value!,
                IccIntent.Saturation => Tags.Has(IccSignatures.AToB2) ? Tags.AToB2.Value! : Tags.AToB0.Value!,
                IccIntent.AbsoluteColorimetric => Tags.Has(IccSignatures.AToB1) ? Tags.AToB1.Value! : Tags.AToB0.Value!,
                _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, message: null),
            };
        }

        return intent switch
        {
            IccIntent.Perceptual => Tags.BToA0.Value!,
            IccIntent.RelativeColorimetric => Tags.Has(IccSignatures.BToA1) ? Tags.BToA1.Value! : Tags.BToA0.Value!,
            IccIntent.Saturation => Tags.Has(IccSignatures.BToA2) ? Tags.BToA2.Value! : Tags.BToA0.Value!,
            IccIntent.AbsoluteColorimetric => Tags.Has(IccSignatures.BToA1) ? Tags.BToA1.Value! : Tags.BToA0.Value!,
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, message: null),
        };
    }

    private static void ApplyLuts(ReadOnlySpan<double> inputValues, IccLuts luts, Span<double> output)
    {
        bool anyNaN = false;
        foreach (double value in inputValues)
        {
            if (double.IsNaN(value))
            {
                anyNaN = true;
                break;
            }
        }

        if (anyNaN)
        {
            output.Fill(double.NaN);
            return;
        }

        switch (luts.Elements)
        {
            case IccLutElements.AB:
                Ab(inputValues, luts.ACurves!, luts.Clut!, luts.BCurves, output);
                break;
            case IccLutElements.BA:
                Ba(inputValues, luts.BCurves, luts.Clut!, luts.ACurves!, output);
                break;
            case IccLutElements.AMB:
                Amb(inputValues, luts.ACurves!, luts.Clut!, luts.MCurves!, luts.Matrices!.Value, luts.BCurves, output);
                break;
            case IccLutElements.BMA:
                Bma(inputValues, luts.BCurves, luts.Matrices!.Value, luts.MCurves!, luts.Clut!, luts.ACurves!, output);
                break;
            case IccLutElements.MB:
                Mb(inputValues, luts.MCurves!, luts.Matrices!.Value, luts.BCurves, output);
                break;
            case IccLutElements.BM:
                Bm(inputValues, luts.BCurves, luts.Matrices!.Value, luts.MCurves!, output);
                break;
            default:
                B(inputValues, luts.BCurves, output);
                break;
        }
    }
}
