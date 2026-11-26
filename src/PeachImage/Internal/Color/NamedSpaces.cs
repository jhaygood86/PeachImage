using PeachImage.Internal.Icc;

namespace PeachImage.Internal.Color;

/// <summary>The primaries, white point and transfer function of every <see cref="NamedColorSpace"/>, and the matrices derived from them.</summary>
internal static class NamedSpaces
{
    public static readonly ChromaticityXy D65 = new(0.3127, 0.3290);

    // The ICC profile connection space white (ICC.1:2010 section 7.2.16), so the matrices below agree with every profile's colorants.
    public static readonly IccVector3 D50White = new(0.9642, 1.0, 0.8249);

    private static readonly ChromaticityXy SrgbRed = new(0.640, 0.330);
    private static readonly ChromaticityXy SrgbGreen = new(0.300, 0.600);
    private static readonly ChromaticityXy SrgbBlue = new(0.150, 0.060);
    private static readonly ChromaticityXy P3Red = new(0.680, 0.320);
    private static readonly ChromaticityXy P3Green = new(0.265, 0.690);
    private static readonly ChromaticityXy P3Blue = new(0.150, 0.060);
    private static readonly ChromaticityXy R2020Red = new(0.708, 0.292);
    private static readonly ChromaticityXy R2020Green = new(0.170, 0.797);
    private static readonly ChromaticityXy R2020Blue = new(0.131, 0.046);

    private static readonly IccMatrix3x3 Bradford = new(
        0.8951, 0.2664, -0.1614,
        -0.7502, 1.7135, 0.0367,
        0.0389, -0.0685, 1.0296);

    private static readonly IccMatrix3x3[] ToXyzD50Cache = new IccMatrix3x3[8];
    private static readonly IccMatrix3x3[] FromXyzD50Cache = new IccMatrix3x3[8];

    static NamedSpaces()
    {
        foreach (var space in Enum.GetValues<NamedColorSpace>())
        {
            var (r, g, b, w) = Primaries(space);
            var toXyzD50 = AdaptToD50(PrimariesToXyz(r, g, b, w), w);
            ToXyzD50Cache[(int)space] = toXyzD50;
            FromXyzD50Cache[(int)space] = toXyzD50.Inverse();
        }
    }

    public static (ChromaticityXy Red, ChromaticityXy Green, ChromaticityXy Blue, ChromaticityXy White) Primaries(NamedColorSpace space) => space switch
    {
        NamedColorSpace.Srgb or NamedColorSpace.LinearSrgb => (SrgbRed, SrgbGreen, SrgbBlue, D65),
        NamedColorSpace.DisplayP3 or NamedColorSpace.LinearDisplayP3 => (P3Red, P3Green, P3Blue, D65),
        NamedColorSpace.Rec2020 or NamedColorSpace.LinearRec2020 or NamedColorSpace.Rec2100Pq or NamedColorSpace.Rec2100Hlg => (R2020Red, R2020Green, R2020Blue, D65),
        _ => throw new ArgumentOutOfRangeException(nameof(space), space, message: null),
    };

    public static TransferFunction Transfer(NamedColorSpace space) => space switch
    {
        NamedColorSpace.Srgb or NamedColorSpace.DisplayP3 => TransferFunction.Srgb,
        NamedColorSpace.LinearSrgb or NamedColorSpace.LinearDisplayP3 or NamedColorSpace.LinearRec2020 => TransferFunction.Linear,
        NamedColorSpace.Rec2020 => TransferFunction.Bt709,
        NamedColorSpace.Rec2100Pq => TransferFunction.Pq,
        NamedColorSpace.Rec2100Hlg => TransferFunction.Hlg,
        _ => throw new ArgumentOutOfRangeException(nameof(space), space, message: null),
    };

    public static ColorSpaceDescription Describe(CicpCode code)
    {
        if (code.ToNamedColorSpace() is { } named)
        {
            return Describe(named);
        }

        var (r, g, b, w) = code.ColorPrimaries switch
        {
            9 => Primaries(NamedColorSpace.Rec2020),
            12 => Primaries(NamedColorSpace.DisplayP3),
            _ => Primaries(NamedColorSpace.Srgb),
        };
        return new ColorSpaceDescription(r, g, b, w, code.Transfer);
    }

    public static ColorSpaceDescription Describe(NamedColorSpace space)
    {
        var (r, g, b, w) = Primaries(space);
        return new ColorSpaceDescription(r, g, b, w, Transfer(space), 0, space);
    }

    /// <summary>The matrix taking linear RGB of <paramref name="space"/> to D50 XYZ.</summary>
    public static IccMatrix3x3 ToXyzD50(NamedColorSpace space) => ToXyzD50Cache[(int)space];

    /// <summary>The matrix taking D50 XYZ to linear RGB of <paramref name="space"/>.</summary>
    public static IccMatrix3x3 FromXyzD50(NamedColorSpace space) => FromXyzD50Cache[(int)space];

    public static bool IsLinear(NamedColorSpace space) => Transfer(space) == TransferFunction.Linear;

    // RGB -> XYZ (in the primaries' own white) from chromaticities.
    private static IccMatrix3x3 PrimariesToXyz(ChromaticityXy r, ChromaticityXy g, ChromaticityXy b, ChromaticityXy w)
    {
        var (xr, yr, zr) = Xyz(r);
        var (xg, yg, zg) = Xyz(g);
        var (xb, yb, zb) = Xyz(b);
        var (xw, yw, zw) = Xyz(w);
        var primaries = new IccMatrix3x3(xr, xg, xb, yr, yg, yb, zr, zg, zb);
        var s = primaries.Inverse().Multiply(new IccVector3(xw, yw, zw));
        return new IccMatrix3x3(
            xr * s.X, xg * s.Y, xb * s.Z,
            yr * s.X, yg * s.Y, yb * s.Z,
            zr * s.X, zg * s.Y, zb * s.Z);
    }

    private static (double X, double Y, double Z) Xyz(ChromaticityXy c) => (c.X / c.Y, 1.0, (1.0 - c.X - c.Y) / c.Y);

    /// <summary>The Bradford matrix taking XYZ under white <paramref name="from"/> to XYZ under the ICC D50 white.</summary>
    public static IccMatrix3x3 BradfordToD50(ChromaticityXy from) => BradfordToD50(Xyz(from));

    private static IccMatrix3x3 BradfordToD50((double X, double Y, double Z) source) => BradfordAdaptation(source, (D50White.X, D50White.Y, D50White.Z));

    /// <summary>The Bradford matrix taking XYZ under the ICC D50 white to XYZ under white <paramref name="to"/>.</summary>
    public static IccMatrix3x3 BradfordFromD50(ChromaticityXy to) => BradfordAdaptation((D50White.X, D50White.Y, D50White.Z), Xyz(to));

    private static IccMatrix3x3 BradfordAdaptation((double X, double Y, double Z) source, (double X, double Y, double Z) destination)
    {
        var s = Bradford.Multiply(new IccVector3(source.X, source.Y, source.Z));
        var d = Bradford.Multiply(new IccVector3(destination.X, destination.Y, destination.Z));
        var scale = new IccMatrix3x3(d.X / s.X, 0, 0, 0, d.Y / s.Y, 0, 0, 0, d.Z / s.Z);
        return Bradford.Inverse().Multiply(scale).Multiply(Bradford);
    }

    private static IccMatrix3x3 AdaptToD50(IccMatrix3x3 rgbToXyz, ChromaticityXy white) => BradfordToD50(white).Multiply(rgbToXyz);

    // ---- transfer functions (scalar, double): encoded -> linear is the EOTF, linear -> encoded the inverse ----

    public static double SrgbToLinear(double e) => e <= 0.04045 ? e / 12.92 : Math.Pow((e + 0.055) / 1.055, 2.4);

    public static double LinearToSrgb(double l) => l <= 0.0031308 ? l * 12.92 : (1.055 * Math.Pow(l, 1 / 2.4)) - 0.055;

    public static double Bt709ToLinear(double e) => e < 0.081 ? e / 4.5 : Math.Pow((e + 0.099) / 1.099, 1 / 0.45);

    public static double LinearToBt709(double l) => l < 0.018 ? 4.5 * l : (1.099 * Math.Pow(l, 0.45)) - 0.099;

    public static double ToLinear(TransferFunction transfer, double gamma, double encoded) => transfer switch
    {
        TransferFunction.Linear => encoded,
        TransferFunction.Srgb => SrgbToLinear(encoded),
        TransferFunction.Bt709 => Bt709ToLinear(encoded),
        TransferFunction.Gamma => Math.Pow(Math.Max(encoded, 0), gamma),
        TransferFunction.Pq => Hdr.PqToLinear(encoded),
        TransferFunction.Hlg => Hdr.HlgToLinear(encoded),
        _ => throw new NotSupportedException($"The transfer function {transfer} cannot be converted."),
    };

    public static double FromLinear(TransferFunction transfer, double gamma, double linear) => transfer switch
    {
        TransferFunction.Linear => linear,
        TransferFunction.Srgb => LinearToSrgb(linear),
        TransferFunction.Bt709 => LinearToBt709(linear),
        TransferFunction.Gamma => Math.Pow(Math.Max(linear, 0), 1 / gamma),
        TransferFunction.Pq => Hdr.LinearToPq(linear),
        TransferFunction.Hlg => Hdr.LinearToHlg(linear),
        _ => throw new NotSupportedException($"The transfer function {transfer} cannot be converted."),
    };
}
