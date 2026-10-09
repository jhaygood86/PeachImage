using System.Numerics;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>
/// Vectorized <c>log2</c>, <c>exp2</c> and <c>pow</c> for positive normal floats, accurate to a few parts in 10^8 (far below one
/// 16-bit output level). Built from <see cref="Vector{T}"/> operations only, so the JIT maps them to AVX2 or NEON.
/// </summary>
internal static class VectorMath
{
    private const float Ln2 = 0.693147180559945f;
    private const float InvLn2 = 1.44269504088896f;

    /// <summary>Base-2 logarithm of positive normal values (other lanes produce unspecified finite results).</summary>
    public static Vector<float> Log2(Vector<float> x)
    {
        var bits = Vector.AsVectorInt32(x);
        var exponent = Vector.ShiftRightArithmetic(bits, 23) - new Vector<int>(127);
        var mantissa = Vector.AsVectorSingle((bits & new Vector<int>(0x007FFFFF)) | new Vector<int>(0x3F800000));

        // Bring the mantissa into [sqrt(1/2), sqrt(2)) so the series argument stays small.
        var large = Vector.GreaterThan(mantissa, new Vector<float>(1.41421356f));
        mantissa = Vector.ConditionalSelect(large, mantissa * new Vector<float>(0.5f), mantissa);
        exponent = Vector.ConditionalSelect(Vector.AsVectorInt32(large), exponent + Vector<int>.One, exponent);

        // ln(m) = 2 atanh(t), t = (m - 1) / (m + 1).
        var t = (mantissa - Vector<float>.One) / (mantissa + Vector<float>.One);
        var t2 = t * t;
        var series = (((new Vector<float>(1f / 9f) * t2) + new Vector<float>(1f / 7f)) * t2 + new Vector<float>(1f / 5f)) * t2;
        series = ((series + new Vector<float>(1f / 3f)) * t2) + Vector<float>.One;
        var ln = new Vector<float>(2f) * t * series;
        return Vector.ConvertToSingle(exponent) + (ln * new Vector<float>(InvLn2));
    }

    /// <summary>2 raised to <paramref name="y"/>, for y within about +-125.</summary>
    public static Vector<float> Exp2(Vector<float> y)
    {
        y = Vector.Min(Vector.Max(y, new Vector<float>(-125f)), new Vector<float>(125f));
        var n = Vector.Floor(y + new Vector<float>(0.5f));
        var g = (y - n) * new Vector<float>(Ln2);

        // e^g on [-ln2/2, ln2/2] (Taylor, degree 7).
        var p = new Vector<float>(1f / 5040f);
        p = (p * g) + new Vector<float>(1f / 720f);
        p = (p * g) + new Vector<float>(1f / 120f);
        p = (p * g) + new Vector<float>(1f / 24f);
        p = (p * g) + new Vector<float>(1f / 6f);
        p = (p * g) + new Vector<float>(0.5f);
        p = (p * g) + Vector<float>.One;
        p = (p * g) + Vector<float>.One;

        var scale = Vector.AsVectorSingle(Vector.ShiftLeft(Vector.ConvertToInt32(n) + new Vector<int>(127), 23));
        return p * scale;
    }

    /// <summary><c>x</c> raised to <paramref name="exponent"/> for positive normal <c>x</c>.</summary>
    public static Vector<float> Pow(Vector<float> x, float exponent) => Exp2(Log2(x) * new Vector<float>(exponent));
}
