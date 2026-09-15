namespace PeachImage.Internal.Icc;

/// <summary>
/// Base type for the ICC device↔PCS transform chosen by <see cref="IccProfile"/> based on which tags are
/// present (<see cref="IccTransformAToB"/>, <see cref="IccTransformTrcMatrix"/>,
/// <see cref="IccTransformTrcGrey"/>, <see cref="IccTransformNone"/>, <see cref="IccTransformDToB"/>). Ported
/// from Wacton/Unicolour's <c>Icc/Transform.cs</c> (MIT license) — see THIRD-PARTY-LICENSES.md. The
/// curve/CLUT/matrix combinators (<see cref="Ab"/>, <see cref="Ba"/>, etc.) are rewritten against
/// <see cref="Span{T}"/> — the upstream versions allocate a fresh array at every stage; these allocate
/// nothing, using caller-supplied (<c>stackalloc</c>-backed) buffers instead, since they run once per pixel.
/// </summary>
internal abstract class IccTransform
{
    /// <summary>The ICC PCS's own D50 white point — deliberately not the profile header's own <c>PcsIlluminant</c>
    /// field (many real profiles don't set it to the spec-mandated D50, so like Unicolour and other reference
    /// implementations, this hardcodes the spec value instead of trusting the header).</summary>
    protected static readonly IccVector3 RefWhite = IccColorMath.IccPcsWhite;

    private static readonly IccVector3 RefBlack = new(0.00336, 0.0034731, 0.00287);

    private readonly IccHeader header;
    private readonly bool hasPerceptualHandling;

    protected readonly IccTags Tags;

    protected IccTransform(IccHeader header, IccTags tags, bool hasPerceptualHandling)
    {
        this.header = header;
        Tags = tags;
        this.hasPerceptualHandling = hasPerceptualHandling;
    }

    private int ProfileVersion => header.ProfileVersion.Major;

    protected bool IsLabPcs => header.Pcs == IccSignatures.Lab;

    private IccVector3 MediaWhite
    {
        get
        {
            if (Tags.MediaWhite.Value is not { } w)
            {
                throw new NotSupportedException("This ICC profile has no media white point (wtpt) tag, which AbsoluteColorimetric rendering intent requires (ICC.1:2010 Annex A.2) to adapt the profile's media white to the PCS illuminant.");
            }

            return new IccVector3(w.X, w.Y, w.Z);
        }
    }

    // Not header.PcsIlluminant! -- per Unicolour's own comment, the spec says the header's illuminant should
    // always equal D50 (4dp), but many real profiles don't adhere to that, so reference implementations
    // (and this port) ignore the custom value and use the fixed spec value instead.
    private static IccVector3 PcsIlluminant => RefWhite;

    /// <summary>Converts <paramref name="deviceValues"/> (device-space, e.g. 4 CMYK channels) to PCS-space XYZ, adapted to the profile's D50 PCS.</summary>
    internal abstract IccVector3 ToXyz(ReadOnlySpan<double> deviceValues, IccIntent intent);

    /// <summary>Converts D50 PCS-space <paramref name="xyz"/> to device-space values, writing them into <paramref name="deviceValues"/>.</summary>
    internal abstract void FromXyz(IccVector3 xyz, IccIntent intent, Span<double> deviceValues);

    /// <summary>
    /// Applies this transform's device→PCS rendering-intent adjustment (<see cref="AdjustXyz"/>) to a PCS
    /// value that didn't itself come from <see cref="ToXyz"/> -- specifically, a profile's own declared
    /// <c>bkpt</c> media black point tag, which ICC.1:2010 stores as an unadjusted colorimetric PCS value.
    /// Black point compensation needs that value in the same (possibly perceptually-adjusted) PCS space every
    /// other value flowing through <see cref="ToXyz"/> under the same intent ends up in, or the two won't be
    /// comparable.
    /// </summary>
    internal IccVector3 AdjustBlackPointXyz(IccVector3 xyz, IccIntent intent) => AdjustXyz(xyz, intent, isDeviceToPcs: true);

    /// <summary>A -&gt; CLUT -&gt; B (device -&gt; CLUT -&gt; PCS, or the reverse).</summary>
    protected static void Ab(ReadOnlySpan<double> aCurveInputs, IccCurve[] aCurves, IccClut clut, IccCurve[] bCurves, Span<double> output)
    {
        Span<double> clutInputs = stackalloc double[aCurves.Length];
        ApplyCurves(aCurves, aCurveInputs, clutInputs);
        Span<double> bCurveInputs = stackalloc double[clut.OutputChannels];
        clut.Lookup(clutInputs, bCurveInputs);
        ApplyCurves(bCurves, bCurveInputs, output);
    }

    /// <summary>B -&gt; CLUT -&gt; A (the reverse direction of <see cref="Ab"/>).</summary>
    protected static void Ba(ReadOnlySpan<double> bCurveInputs, IccCurve[] bCurves, IccClut clut, IccCurve[] aCurves, Span<double> output)
    {
        Span<double> clutInputs = stackalloc double[bCurves.Length];
        ApplyCurves(bCurves, bCurveInputs, clutInputs);
        Span<double> aCurveInputs = stackalloc double[clut.OutputChannels];
        clut.Lookup(clutInputs, aCurveInputs);
        ApplyCurves(aCurves, aCurveInputs, output);
    }

    /// <summary>A -&gt; CLUT -&gt; M -&gt; Matrix -&gt; B.</summary>
    protected static void Amb(ReadOnlySpan<double> aCurveInputs, IccCurve[] aCurves, IccClut clut, IccCurve[] mCurves, IccMatrices matrices, IccCurve[] bCurves, Span<double> output)
    {
        Span<double> clutInputs = stackalloc double[aCurves.Length];
        ApplyCurves(aCurves, aCurveInputs, clutInputs);
        Span<double> mCurveInputs = stackalloc double[clut.OutputChannels];
        clut.Lookup(clutInputs, mCurveInputs);
        Span<double> matrixInputs = stackalloc double[3];
        ApplyCurves(mCurves, mCurveInputs, matrixInputs);
        var matrixOutput = matrices.Apply(new IccVector3(matrixInputs[0], matrixInputs[1], matrixInputs[2]));
        Span<double> bCurveInputs = [matrixOutput.X, matrixOutput.Y, matrixOutput.Z];
        ApplyCurves(bCurves, bCurveInputs, output);
    }

    /// <summary>B -&gt; Matrix -&gt; M -&gt; CLUT -&gt; A (the reverse direction of <see cref="Amb"/>).</summary>
    protected static void Bma(ReadOnlySpan<double> bCurveInputs, IccCurve[] bCurves, IccMatrices matrices, IccCurve[] mCurves, IccClut clut, IccCurve[] aCurves, Span<double> output)
    {
        Span<double> matrixInputs = stackalloc double[3];
        ApplyCurves(bCurves, bCurveInputs, matrixInputs);
        var matrixOutput = matrices.Apply(new IccVector3(matrixInputs[0], matrixInputs[1], matrixInputs[2]));
        Span<double> mCurveInputs = [matrixOutput.X, matrixOutput.Y, matrixOutput.Z];
        Span<double> clutInputs = stackalloc double[mCurves.Length];
        ApplyCurves(mCurves, mCurveInputs, clutInputs);
        Span<double> aCurveInputs = stackalloc double[clut.OutputChannels];
        clut.Lookup(clutInputs, aCurveInputs);
        ApplyCurves(aCurves, aCurveInputs, output);
    }

    /// <summary>M -&gt; Matrix -&gt; B.</summary>
    protected static void Mb(ReadOnlySpan<double> mCurveInputs, IccCurve[] mCurves, IccMatrices matrices, IccCurve[] bCurves, Span<double> output)
    {
        Span<double> matrixInputs = stackalloc double[3];
        ApplyCurves(mCurves, mCurveInputs, matrixInputs);
        var matrixOutput = matrices.Apply(new IccVector3(matrixInputs[0], matrixInputs[1], matrixInputs[2]));
        Span<double> bCurveInputs = [matrixOutput.X, matrixOutput.Y, matrixOutput.Z];
        ApplyCurves(bCurves, bCurveInputs, output);
    }

    /// <summary>B -&gt; Matrix -&gt; M (the reverse direction of <see cref="Mb"/>).</summary>
    protected static void Bm(ReadOnlySpan<double> bCurveInputs, IccCurve[] bCurves, IccMatrices matrices, IccCurve[] mCurves, Span<double> output)
    {
        Span<double> matrixInputs = stackalloc double[3];
        ApplyCurves(bCurves, bCurveInputs, matrixInputs);
        var matrixOutput = matrices.Apply(new IccVector3(matrixInputs[0], matrixInputs[1], matrixInputs[2]));
        Span<double> mCurveInputs = [matrixOutput.X, matrixOutput.Y, matrixOutput.Z];
        ApplyCurves(mCurves, mCurveInputs, output);
    }

    /// <summary>Just the B curves, with no matrix or CLUT stage.</summary>
    protected static void B(ReadOnlySpan<double> bCurveInputs, IccCurve[] bCurves, Span<double> output) => ApplyCurves(bCurves, bCurveInputs, output);

    private static void ApplyCurves(IccCurve[] curves, ReadOnlySpan<double> inputs, Span<double> outputs)
    {
        for (int i = 0; i < curves.Length; i++)
        {
            // Default to 0.0 if not enough channels were provided (e.g. a 7-channel profile fed only 4 inputs).
            double input = i < inputs.Length ? inputs[i] : 0.0;
            outputs[i] = curves[i].Lookup(input);
        }
    }

    private enum PcsAdjustment
    {
        None,
        Perceptual,
        AbsoluteColorimetric,
    }

    protected IccVector3 AdjustXyz(IccVector3 xyz, IccIntent intent, bool isDeviceToPcs)
    {
        var pcsAdjustment = intent switch
        {
            IccIntent.Perceptual when ProfileVersion == 2 || !hasPerceptualHandling => PcsAdjustment.Perceptual,
            IccIntent.AbsoluteColorimetric => PcsAdjustment.AbsoluteColorimetric,
            _ => PcsAdjustment.None,
        };

        /*
         * When using a Lab PCS, negative values are clipped before the PCS adjustment; when using an XYZ PCS,
         * they're clipped after. Mirrors DemoIccMAX's CIccXform::AdjustPCS (which clips before/after via
         * CheckSrcAbs/CheckDstAbs depending on direction and PCS), per Unicolour's own comment making the
         * same observation.
         */
        if (IsLabPcs && pcsAdjustment != PcsAdjustment.None)
        {
            xyz = NegativeClip(xyz);
        }

        xyz = pcsAdjustment switch
        {
            PcsAdjustment.Perceptual => AdjustXyzPerceptual(xyz, isDeviceToPcs),
            PcsAdjustment.AbsoluteColorimetric => AdjustXyzAbsolute(xyz, isDeviceToPcs),
            _ => xyz,
        };

        if (!IsLabPcs && pcsAdjustment != PcsAdjustment.None)
        {
            xyz = NegativeClip(xyz);
        }

        return xyz;
    }

    private static IccVector3 AdjustXyzPerceptual(IccVector3 xyz, bool isDeviceToPcs)
    {
        double Adjust(double value, double refBlack, double refWhite)
        {
            double scale = isDeviceToPcs ? 1 - (refBlack / refWhite) : 1 / (1 - (refBlack / refWhite));
            double offset = isDeviceToPcs ? refBlack : -refBlack * scale;
            return (value * scale) + offset;
        }

        return new IccVector3(
            Adjust(xyz.X, RefBlack.X, RefWhite.X),
            Adjust(xyz.Y, RefBlack.Y, RefWhite.Y),
            Adjust(xyz.Z, RefBlack.Z, RefWhite.Z));
    }

    private IccVector3 AdjustXyzAbsolute(IccVector3 xyz, bool isDeviceToPcs)
    {
        var mediaWhite = MediaWhite;
        var illuminant = PcsIlluminant;
        double Scale(double media, double illum) => isDeviceToPcs ? media / illum : illum / media;

        return new IccVector3(
            xyz.X * Scale(mediaWhite.X, illuminant.X),
            xyz.Y * Scale(mediaWhite.Y, illuminant.Y),
            xyz.Z * Scale(mediaWhite.Z, illuminant.Z));
    }

    internal static IccVector3 IccLabToLab(IccVector3 iccLab) => new(
        iccLab.X * 100,
        (iccLab.Y * (127 + 128)) - 128,
        (iccLab.Z * (127 + 128)) - 128);

    internal static IccVector3 LabToIccLab(IccVector3 lab) => new(
        lab.X / 100.0,
        (lab.Y + 128) / (127 + 128),
        (lab.Z + 128) / (127 + 128));

    internal static IccVector3 IccLab2ToIccLab4(IccVector3 iccLab2) => new(iccLab2.X * 65535.0 / 65280.0, iccLab2.Y * 65535.0 / 65280.0, iccLab2.Z * 65535.0 / 65280.0);

    internal static IccVector3 IccLab4ToIccLab2(IccVector3 iccLab4) => new(iccLab4.X * 65280.0 / 65535.0, iccLab4.Y * 65280.0 / 65535.0, iccLab4.Z * 65280.0 / 65535.0);

    internal static IccVector3 XyzToIccXyz(IccVector3 xyz) => new(xyz.X * 32768.0 / 65535.0, xyz.Y * 32768.0 / 65535.0, xyz.Z * 32768.0 / 65535.0);

    internal static IccVector3 IccXyzToXyz(IccVector3 iccXyz) => new(iccXyz.X * 65535.0 / 32768.0, iccXyz.Y * 65535.0 / 32768.0, iccXyz.Z * 65535.0 / 32768.0);

    internal static IccVector3 LabToXyz(IccVector3 lab) => IccColorMath.LabToXyz(lab, RefWhite);

    internal static IccVector3 XyzToLab(IccVector3 xyz) => IccColorMath.XyzToLab(xyz, RefWhite);

    private static IccVector3 NegativeClip(IccVector3 xyz) => new(Math.Max(xyz.X, 0), Math.Max(xyz.Y, 0), Math.Max(xyz.Z, 0));
}
