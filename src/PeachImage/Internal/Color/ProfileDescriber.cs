using PeachImage.Internal.Icc;

namespace PeachImage.Internal.Color;

/// <summary>Summarizes an RGB matrix/TRC ICC profile as primaries, white point and transfer function, and matches it to a <see cref="NamedColorSpace"/>.</summary>
internal static class ProfileDescriber
{
    private const double PrimaryTolerance = 0.003;
    private const double CurveTolerance = 0.004;

    private static readonly NamedColorSpace[] Candidates =
    [
        NamedColorSpace.Srgb, NamedColorSpace.LinearSrgb, NamedColorSpace.DisplayP3, NamedColorSpace.LinearDisplayP3, NamedColorSpace.Rec2020, NamedColorSpace.LinearRec2020,
    ];

    public static ColorSpaceDescription? Describe(IccProfile profile)
    {
        if (profile.DataColorSpace != IccSignatures.Rgb || !profile.TryGetRgbMatrixTrc(out var toXyzD50, out var curves))
        {
            return null;
        }

        var toD65 = NamedSpaces.BradfordFromD50(NamedSpaces.D65).Multiply(toXyzD50).ToArray();
        var red = Chromaticity(toD65[0], toD65[3], toD65[6]);
        var green = Chromaticity(toD65[1], toD65[4], toD65[7]);
        var blue = Chromaticity(toD65[2], toD65[5], toD65[8]);
        var white = Chromaticity(toD65[0] + toD65[1] + toD65[2], toD65[3] + toD65[4] + toD65[5], toD65[6] + toD65[7] + toD65[8]);

        var (transfer, gamma) = Classify(curves);
        NamedColorSpace? named = null;
        if (transfer != TransferFunction.Unknown)
        {
            foreach (var candidate in Candidates)
            {
                var (r, g, b, w) = NamedSpaces.Primaries(candidate);
                if (NamedSpaces.Transfer(candidate) == transfer
                    && Near(red, r) && Near(green, g) && Near(blue, b) && Near(white, w))
                {
                    named = candidate;
                    break;
                }
            }
        }

        return new ColorSpaceDescription(red, green, blue, white, transfer, transfer == TransferFunction.Gamma ? gamma : 0, named);
    }

    private static ChromaticityXy Chromaticity(double x, double y, double z)
    {
        double sum = x + y + z;
        return sum <= 0 ? default : new ChromaticityXy(x / sum, y / sum);
    }

    private static bool Near(ChromaticityXy a, ChromaticityXy b) => Math.Abs(a.X - b.X) <= PrimaryTolerance && Math.Abs(a.Y - b.Y) <= PrimaryTolerance;

    private static (TransferFunction Transfer, double Gamma) Classify(IccCurve[] curves)
    {
        const int samples = 65;
        var fits = new[] { TransferFunction.Linear, TransferFunction.Srgb, TransferFunction.Bt709 };
        foreach (var fit in fits)
        {
            bool all = true;
            foreach (var curve in curves)
            {
                for (int i = 0; i < samples && all; i++)
                {
                    double x = i / (double)(samples - 1);
                    all = Math.Abs(curve.Lookup(x) - NamedSpaces.ToLinear(fit, 0, x)) <= CurveTolerance;
                }
            }

            if (all)
            {
                return (fit, 0);
            }
        }

        // A pure power law: fit the exponent at mid-gray and check the whole curve against it.
        double g = Math.Log(Math.Max(curves[0].Lookup(0.5), 1e-9)) / Math.Log(0.5);
        if (g is > 0.5 and < 6.0)
        {
            bool all = true;
            foreach (var curve in curves)
            {
                for (int i = 0; i < samples && all; i++)
                {
                    double x = i / (double)(samples - 1);
                    all = Math.Abs(curve.Lookup(x) - Math.Pow(x, g)) <= CurveTolerance;
                }
            }

            if (all)
            {
                return (TransferFunction.Gamma, Math.Round(g, 3));
            }
        }

        return (TransferFunction.Unknown, 0);
    }
}
