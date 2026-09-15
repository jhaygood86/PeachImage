namespace PeachImage.Internal.Icc;

/// <summary>
/// The color math PeachImage needs downstream of the ported ICC engine, none of it ported from
/// Wacton/Unicolour: the standard CIE Lab↔XYZ formulas (used by <see cref="IccTransform"/> in the same shape
/// Unicolour's own <c>Lab.cs</c> uses them, but reimplemented here against <see cref="IccVector3"/> instead of
/// importing Unicolour's general <c>Lab</c>/<c>Xyz</c>/<c>WhitePoint</c> types), plus a fixed
/// Bradford-chromatic-adaptation-then-XYZ-to-sRGB pipeline. PeachImage only ever needs one fixed output space
/// (sRGB), unlike Unicolour's general, pluggable multi-white-point <c>Configuration</c>/<c>ChromaticAdaptor</c>
/// system — so rather than port that machinery, the two fixed 3x3 matrices it would otherwise chain (ICC's
/// D50 profile-connection-space to sRGB's own D65 reference white, then D65 XYZ to linear sRGB) are combined
/// into a single matrix once, computed as a <see langword="static readonly"/> field rather than per pixel.
/// </summary>
internal static class IccColorMath
{
    /// <summary>The ICC PCS's own D50 white point (ICC.1:2010's fixed, slightly-rounded value — not a generic colorimetric D50).</summary>
    internal static readonly IccVector3 IccPcsWhite = new(0.9642, 1.0000, 0.8249);

    private const double LabDelta = 6.0 / 29.0;

    /// <summary>CIE XYZ to CIE Lab (https://en.wikipedia.org/wiki/CIELAB_color_space#From_CIEXYZ_to_CIELAB).</summary>
    internal static IccVector3 XyzToLab(IccVector3 xyz, IccVector3 whitePoint)
    {
        double xr = xyz.X / whitePoint.X;
        double yr = xyz.Y / whitePoint.Y;
        double zr = xyz.Z / whitePoint.Z;
        double fx = LabF(xr);
        double fy = LabF(yr);
        double fz = LabF(zr);

        double l = (116 * fy) - 16;
        double a = 500 * (fx - fy);
        double b = 200 * (fy - fz);
        return new IccVector3(l, a, b);
    }

    /// <summary>CIE Lab to CIE XYZ (https://en.wikipedia.org/wiki/CIELAB_color_space#From_CIELAB_to_CIEXYZ).</summary>
    internal static IccVector3 LabToXyz(IccVector3 lab, IccVector3 whitePoint)
    {
        double fy = (lab.X + 16) / 116.0;
        double fx = fy + (lab.Y / 500.0);
        double fz = fy - (lab.Z / 200.0);

        double x = whitePoint.X * LabFInverse(fx);
        double y = whitePoint.Y * LabFInverse(fy);
        double z = whitePoint.Z * LabFInverse(fz);
        return new IccVector3(x, y, z);
    }

    private static double LabF(double t) => t > LabDelta * LabDelta * LabDelta ? Math.Cbrt(t) : (t / (3 * LabDelta * LabDelta)) + (4.0 / 29.0);

    private static double LabFInverse(double t) => t > LabDelta ? t * t * t : 3 * LabDelta * LabDelta * (t - (4.0 / 29.0));

    // Bradford chromatic adaptation matrix, ICC PCS D50 -> sRGB's reference D65 white (the standard,
    // widely-published matrix used across color-management implementations for this exact adaptation, e.g.
    // when deriving the sRGB ICC profile itself; see Bruce Lindbloom's chromatic adaptation reference).
    private static readonly IccMatrix3x3 BradfordD50ToD65 = new(
        0.9555766, -0.0230393, 0.0631636,
        -0.0282895, 1.0099416, 0.0210077,
        0.0122982, -0.0204830, 1.3299098);

    // The standard XYZ(D65) -> linear sRGB matrix (IEC 61966-2-1).
    private static readonly IccMatrix3x3 XyzD65ToLinearSrgb = new(
        3.2404542, -1.5371385, -0.4985314,
        -0.9692660, 1.8760108, 0.0415560,
        0.0556434, -0.2040259, 1.0572252);

    /// <summary>
    /// The fused ICC-PCS-D50-XYZ to linear-sRGB matrix: Bradford-adapts D50 to D65, then applies the standard
    /// XYZ(D65)-to-linear-sRGB matrix, as a single 3x3 multiply. Computed once, not per pixel.
    /// </summary>
    internal static readonly IccMatrix3x3 XyzD50ToLinearSrgb = XyzD65ToLinearSrgb.Multiply(BradfordD50ToD65);

    /// <summary>sRGB's OETF (opto-electronic transfer function) gamma companding, clamping out-of-gamut values to [0, 1] first.</summary>
    internal static byte LinearToSrgbByte(double linear)
    {
        double clamped = Math.Clamp(linear, 0.0, 1.0);
        double companded = clamped <= 0.0031308 ? clamped * 12.92 : (1.055 * Math.Pow(clamped, 1 / 2.4)) - 0.055;
        return NormalizedToByte(companded);
    }

    /// <summary>Scales a normalized (0-1, but not clamped going in) value to a byte, rounding and clamping to [0, 255].</summary>
    internal static byte NormalizedToByte(double normalized) => (byte)Math.Clamp(Math.Round(normalized * 255.0), 0, 255);
}
