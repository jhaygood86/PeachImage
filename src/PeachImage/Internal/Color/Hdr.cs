namespace PeachImage.Internal.Color;

/// <summary>
/// The HDR transfer functions in this library's float convention: linear 1.0 is SDR reference white, 203 cd/m2 (ITU-R BT.2408), so an
/// HDR signal's headroom shows as values above 1.
/// </summary>
internal static class Hdr
{
    /// <summary>The luminance of reference white in cd/m2 (ITU-R BT.2408).</summary>
    public const double ReferenceWhiteNits = 203.0;

    /// <summary>The luminance PQ's 1.0 stands for in cd/m2.</summary>
    public const double PqPeakNits = 10000.0;

    /// <summary>The nominal peak of an HLG display in cd/m2 (ITU-R BT.2100 reference display).</summary>
    public const double HlgPeakNits = 1000.0;

    private const double M1 = 2610.0 / 16384.0;
    private const double M2 = 2523.0 / 4096.0 * 128.0;
    private const double C1 = 3424.0 / 4096.0;
    private const double C2 = 2413.0 / 4096.0 * 32.0;
    private const double C3 = 2392.0 / 4096.0 * 32.0;

    private const double HlgA = 0.17883277;
    private const double HlgB = 1.0 - (4.0 * HlgA);
    private static readonly double HlgC = 0.5 - (HlgA * Math.Log(4.0 * HlgA));

    /// <summary>PQ signal in 0..1 to linear light where 1.0 is reference white.</summary>
    public static double PqToLinear(double encoded)
    {
        double p = Math.Pow(Math.Clamp(encoded, 0.0, 1.0), 1.0 / M2);
        double y = Math.Pow(Math.Max(p - C1, 0.0) / (C2 - (C3 * p)), 1.0 / M1);
        return y * PqPeakNits / ReferenceWhiteNits;
    }

    /// <summary>Linear light where 1.0 is reference white to a PQ signal in 0..1.</summary>
    public static double LinearToPq(double linear)
    {
        double y = Math.Clamp(linear * ReferenceWhiteNits / PqPeakNits, 0.0, 1.0);
        double ym = Math.Pow(y, M1);
        return Math.Pow((C1 + (C2 * ym)) / (1.0 + (C3 * ym)), M2);
    }

    /// <summary>The inverse HLG OETF: a signal in 0..1 to relative scene light in 0..1.</summary>
    public static double HlgInverseOetf(double encoded)
    {
        double e = Math.Clamp(encoded, 0.0, 1.0);
        return e <= 0.5 ? e * e / 3.0 : (Math.Exp((e - HlgC) / HlgA) + HlgB) / 12.0;
    }

    /// <summary>The HLG OETF: relative scene light in 0..1 to a signal in 0..1.</summary>
    public static double HlgOetf(double scene)
    {
        double s = Math.Clamp(scene, 0.0, 1.0);
        return s <= 1.0 / 12.0 ? Math.Sqrt(3.0 * s) : (HlgA * Math.Log((12.0 * s) - HlgB)) + HlgC;
    }

    /// <summary>
    /// An HLG signal to relative scene light in 0..1 (the inverse OETF). HLG is scene-referred: the pipeline then applies the system OOTF (see
    /// <see cref="ColorKernels.HlgOotf"/>) to get display light where 1.0 is reference white.
    /// </summary>
    public static double HlgToLinear(double encoded) => HlgInverseOetf(encoded);

    /// <summary>The inverse of <see cref="HlgToLinear"/>.</summary>
    public static double LinearToHlg(double linear) => HlgOetf(linear);
}
