using System.Runtime.Intrinsics;

namespace PeachImage.Internal.Icc;

/// <summary>A plain 3-element vector — CMYK's PCS side (Lab/XYZ) and every ICC matrix stage are always exactly 3D.</summary>
internal readonly struct IccVector3(double x, double y, double z)
{
    internal double X { get; } = x;

    internal double Y { get; } = y;

    internal double Z { get; } = z;

    public static IccVector3 operator +(IccVector3 a, IccVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
}

/// <summary>
/// A 3x3 matrix. Purpose-built value type (no heap allocation, unlike Wacton/Unicolour's general
/// array-backed <c>Matrix</c> class it replaces — see THIRD-PARTY-LICENSES.md) since every ICC matrix stage
/// this engine supports (the RGB-matrix-TRC transform, and the "M" matrix inside an <c>mAB </c>/<c>mBA </c>
/// LUT) is exactly 3x3, connecting to a 3-channel PCS (Lab or XYZ).
/// </summary>
internal readonly struct IccMatrix3x3(
    double m00, double m01, double m02,
    double m10, double m11, double m12,
    double m20, double m21, double m22)
{
    private readonly double m00 = m00, m01 = m01, m02 = m02;
    private readonly double m10 = m10, m11 = m11, m12 = m12;
    private readonly double m20 = m20, m21 = m21, m22 = m22;

    internal IccVector3 Multiply(IccVector3 v) => new(
        (m00 * v.X) + (m01 * v.Y) + (m02 * v.Z),
        (m10 * v.X) + (m11 * v.Y) + (m12 * v.Z),
        (m20 * v.X) + (m21 * v.Y) + (m22 * v.Z));

    /// <summary>
    /// Applies this matrix to a batch of <see cref="Vector256{T}"/>-width (4) vectors at once — each of
    /// <paramref name="x"/>/<paramref name="y"/>/<paramref name="z"/> holds one coordinate from 4 different
    /// input vectors, laid out "structure of arrays" style so the multiply-adds below are genuine SIMD
    /// rather than 4 independent scalar multiplies. This is the one part of the ICC→sRGB pipeline that's pure,
    /// fixed linear algebra (unlike the profile's own device→PCS CLUT/curve evaluation, which is inherently
    /// data-dependent per pixel and not vectorized this way).
    /// </summary>
    internal (Vector256<double> R, Vector256<double> G, Vector256<double> B) MultiplyBatch(Vector256<double> x, Vector256<double> y, Vector256<double> z)
    {
        var r = (Vector256.Create(m00) * x) + (Vector256.Create(m01) * y) + (Vector256.Create(m02) * z);
        var g = (Vector256.Create(m10) * x) + (Vector256.Create(m11) * y) + (Vector256.Create(m12) * z);
        var b = (Vector256.Create(m20) * x) + (Vector256.Create(m21) * y) + (Vector256.Create(m22) * z);
        return (r, g, b);
    }

    /// <summary>The <see cref="Vector128{T}"/>-width (2) tier of <see cref="MultiplyBatch(Vector256{double}, Vector256{double}, Vector256{double})"/>.</summary>
    internal (Vector128<double> R, Vector128<double> G, Vector128<double> B) MultiplyBatch(Vector128<double> x, Vector128<double> y, Vector128<double> z)
    {
        var r = (Vector128.Create(m00) * x) + (Vector128.Create(m01) * y) + (Vector128.Create(m02) * z);
        var g = (Vector128.Create(m10) * x) + (Vector128.Create(m11) * y) + (Vector128.Create(m12) * z);
        var b = (Vector128.Create(m20) * x) + (Vector128.Create(m21) * y) + (Vector128.Create(m22) * z);
        return (r, g, b);
    }

    internal IccMatrix3x3 Multiply(IccMatrix3x3 other) => new(
        (m00 * other.m00) + (m01 * other.m10) + (m02 * other.m20),
        (m00 * other.m01) + (m01 * other.m11) + (m02 * other.m21),
        (m00 * other.m02) + (m01 * other.m12) + (m02 * other.m22),
        (m10 * other.m00) + (m11 * other.m10) + (m12 * other.m20),
        (m10 * other.m01) + (m11 * other.m11) + (m12 * other.m21),
        (m10 * other.m02) + (m11 * other.m12) + (m12 * other.m22),
        (m20 * other.m00) + (m21 * other.m10) + (m22 * other.m20),
        (m20 * other.m01) + (m21 * other.m11) + (m22 * other.m21),
        (m20 * other.m02) + (m21 * other.m12) + (m22 * other.m22));

    internal IccMatrix3x3 Inverse()
    {
        double a = m00, b = m01, c = m02;
        double d = m10, e = m11, f = m12;
        double g = m20, h = m21, i = m22;

        double determinant = (a * e * i) + (b * f * g) + (c * d * h) - (c * e * g) - (a * f * h) - (b * d * i);
        if (determinant == 0)
        {
            return new IccMatrix3x3(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        }

        double invDet = 1.0 / determinant;
        return new IccMatrix3x3(
            (e * i) - (f * h), (h * c) - (i * b), (b * f) - (c * e),
            (g * f) - (d * i), (a * i) - (g * c), (d * c) - (a * f),
            (d * h) - (g * e), (g * b) - (a * h), (a * e) - (d * b)).Scale(invDet);
    }

    private IccMatrix3x3 Scale(double scalar) => new(
        m00 * scalar, m01 * scalar, m02 * scalar,
        m10 * scalar, m11 * scalar, m12 * scalar,
        m20 * scalar, m21 * scalar, m22 * scalar);
}
