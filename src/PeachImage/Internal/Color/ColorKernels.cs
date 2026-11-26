using System.Numerics;

namespace PeachImage.Internal.Color;

/// <summary>
/// Element-wise float kernels over planar channel spans (all of one channel's samples contiguous), so every operation is a straight
/// vector loop with no shuffles. They are written against <see cref="Vector{T}"/>, which the runtime sizes to the hardware (256-bit
/// with AVX2, 128-bit with NEON or SSE) so the same source covers x86 and ARM; each has a scalar twin the tests compare against, and the
/// scalar loop also finishes the tail of every span.
/// </summary>
/// <remarks>
/// The transfer functions use polynomial log2/exp2 (absolute error below 2e-7 on the log2 and a relative error below 2e-7 on exp2), so a
/// result is within about 1e-6 of the double-precision function for values in 0..1 and scales relatively beyond; that is tighter than one
/// step of 16-bit output. Negative inputs are mirrored (f(-x) = -f(x)), which keeps extended-range float data continuous.
/// </remarks>
internal static class ColorKernels
{
    internal static readonly Vector<float> SignMask = Vector.AsVectorSingle(new Vector<int>(unchecked((int)0x80000000)));

    /// <summary>Whether the vector path is used; tests force the scalar path by calling the <c>*Scalar</c> twins directly.</summary>
    public static bool Accelerated => Vector.IsHardwareAccelerated;

    // ---------------------------------------------------------------- matrix

    /// <summary>Multiplies each (r, g, b) triple of the three planes by the row-major 3x3 <paramref name="m"/>, in place.</summary>
    public static void Matrix3x3(Span<float> r, Span<float> g, Span<float> b, ReadOnlySpan<float> m)
    {
        int n = r.Length;
        int i = 0;
        if (Accelerated && n >= Vector<float>.Count)
        {
            var m00 = new Vector<float>(m[0]);
            var m01 = new Vector<float>(m[1]);
            var m02 = new Vector<float>(m[2]);
            var m10 = new Vector<float>(m[3]);
            var m11 = new Vector<float>(m[4]);
            var m12 = new Vector<float>(m[5]);
            var m20 = new Vector<float>(m[6]);
            var m21 = new Vector<float>(m[7]);
            var m22 = new Vector<float>(m[8]);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                var vr = new Vector<float>(r[i..]);
                var vg = new Vector<float>(g[i..]);
                var vb = new Vector<float>(b[i..]);
                (vr * m00 + vg * m01 + vb * m02).CopyTo(r[i..]);
                (vr * m10 + vg * m11 + vb * m12).CopyTo(g[i..]);
                (vr * m20 + vg * m21 + vb * m22).CopyTo(b[i..]);
            }
        }

        Matrix3x3Scalar(r[i..], g[i..], b[i..], m);
    }

    public static void Matrix3x3Scalar(Span<float> r, Span<float> g, Span<float> b, ReadOnlySpan<float> m)
    {
        for (int i = 0; i < r.Length; i++)
        {
            float vr = r[i], vg = g[i], vb = b[i];
            r[i] = (vr * m[0]) + (vg * m[1]) + (vb * m[2]);
            g[i] = (vr * m[3]) + (vg * m[4]) + (vb * m[5]);
            b[i] = (vr * m[6]) + (vg * m[7]) + (vb * m[8]);
        }
    }

    // ---------------------------------------------------------------- clamp / premultiply

    /// <summary>Clamps every sample to 0..1 (NaN becomes 0).</summary>
    public static void ClampUnit(Span<float> v)
    {
        int i = 0;
        if (Accelerated && v.Length >= Vector<float>.Count)
        {
            var zero = Vector<float>.Zero;
            var one = Vector<float>.One;
            for (; i <= v.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var x = new Vector<float>(v[i..]);
                // Max against zero first with a self-compare so NaN lanes become 0.
                var valid = Vector.Equals(x, x);
                x = Vector.ConditionalSelect(valid, x, zero);
                Vector.Min(Vector.Max(x, zero), one).CopyTo(v[i..]);
            }
        }

        ClampUnitScalar(v[i..]);
    }

    public static void ClampUnitScalar(Span<float> v)
    {
        for (int i = 0; i < v.Length; i++)
        {
            float x = v[i];
            v[i] = x > 0f ? (x < 1f ? x : 1f) : 0f;
        }
    }

    /// <summary>Multiplies the color planes by the alpha plane, in place.</summary>
    public static void Premultiply(Span<float> r, Span<float> g, Span<float> b, ReadOnlySpan<float> a)
    {
        int i = 0;
        if (Accelerated && r.Length >= Vector<float>.Count)
        {
            for (; i <= r.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var va = new Vector<float>(a[i..]);
                (new Vector<float>(r[i..]) * va).CopyTo(r[i..]);
                (new Vector<float>(g[i..]) * va).CopyTo(g[i..]);
                (new Vector<float>(b[i..]) * va).CopyTo(b[i..]);
            }
        }

        PremultiplyScalar(r[i..], g[i..], b[i..], a[i..]);
    }

    public static void PremultiplyScalar(Span<float> r, Span<float> g, Span<float> b, ReadOnlySpan<float> a)
    {
        for (int i = 0; i < r.Length; i++)
        {
            float alpha = a[i];
            r[i] *= alpha;
            g[i] *= alpha;
            b[i] *= alpha;
        }
    }

    /// <summary>Divides the color planes by the alpha plane, in place; where alpha is 0 the color becomes 0.</summary>
    public static void Unpremultiply(Span<float> r, Span<float> g, Span<float> b, ReadOnlySpan<float> a)
    {
        int i = 0;
        if (Accelerated && r.Length >= Vector<float>.Count)
        {
            var zero = Vector<float>.Zero;
            var one = Vector<float>.One;
            for (; i <= r.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var va = new Vector<float>(a[i..]);
                var nonZero = Vector.GreaterThan(va, zero);
                var inv = Vector.ConditionalSelect(nonZero, one / va, zero);
                (new Vector<float>(r[i..]) * inv).CopyTo(r[i..]);
                (new Vector<float>(g[i..]) * inv).CopyTo(g[i..]);
                (new Vector<float>(b[i..]) * inv).CopyTo(b[i..]);
            }
        }

        UnpremultiplyScalar(r[i..], g[i..], b[i..], a[i..]);
    }

    public static void UnpremultiplyScalar(Span<float> r, Span<float> g, Span<float> b, ReadOnlySpan<float> a)
    {
        for (int i = 0; i < r.Length; i++)
        {
            float alpha = a[i];
            float inv = alpha > 0f ? 1f / alpha : 0f;
            r[i] *= inv;
            g[i] *= inv;
            b[i] *= inv;
        }
    }

    // ---------------------------------------------------------------- HDR: HLG system gamma and tone mapping

    // BT.2100 luminance coefficients (Rec. 2020 primaries), the space every HDR source here is in.
    private const float LumaR = 0.2627f;
    private const float LumaG = 0.6780f;
    private const float LumaB = 0.0593f;

    // BT.2100 HLG system gamma for a 1000 cd/m2 display and the display peak in reference-white units.
    private const float HlgGamma = 1.2f;
    private const float HlgAlpha = (float)(Hdr.HlgPeakNits / Hdr.ReferenceWhiteNits);

    /// <summary>
    /// The HLG OOTF (ITU-R BT.2100): turns relative scene light (0..1 per channel) into display light in reference-white units,
    /// <c>D = (1000/203) x Ys^0.2 x E</c> with <c>Ys</c> the BT.2100 luminance of the scene light, in place.
    /// </summary>
    public static void HlgOotf(Span<float> r, Span<float> g, Span<float> b)
    {
        int i = 0;
        if (Accelerated && r.Length >= Vector<float>.Count)
        {
            for (; i <= r.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var vr = new Vector<float>(r[i..]);
                var vg = new Vector<float>(g[i..]);
                var vb = new Vector<float>(b[i..]);
                var ys = (vr * LumaR) + (vg * LumaG) + (vb * LumaB);
                var factor = Pow(Vector.Max(ys, Vector<float>.Zero), new Vector<float>(HlgGamma - 1f)) * HlgAlpha;
                (vr * factor).CopyTo(r[i..]);
                (vg * factor).CopyTo(g[i..]);
                (vb * factor).CopyTo(b[i..]);
            }
        }

        HlgOotfScalar(r[i..], g[i..], b[i..]);
    }

    public static void HlgOotfScalar(Span<float> r, Span<float> g, Span<float> b)
    {
        for (int i = 0; i < r.Length; i++)
        {
            double ys = Math.Max((LumaR * r[i]) + (LumaG * g[i]) + (LumaB * b[i]), 0.0);
            float factor = (float)(HlgAlpha * (ys > 0 ? Math.Pow(ys, HlgGamma - 1.0) : 0.0));
            r[i] *= factor;
            g[i] *= factor;
            b[i] *= factor;
        }
    }

    /// <summary>The inverse of <see cref="HlgOotf"/>: display light in reference-white units back to relative scene light, in place.</summary>
    public static void HlgInverseOotf(Span<float> r, Span<float> g, Span<float> b)
    {
        int i = 0;
        if (Accelerated && r.Length >= Vector<float>.Count)
        {
            for (; i <= r.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var vr = new Vector<float>(r[i..]);
                var vg = new Vector<float>(g[i..]);
                var vb = new Vector<float>(b[i..]);
                var yd = Vector.Max(((vr * LumaR) + (vg * LumaG) + (vb * LumaB)) * (1f / HlgAlpha), Vector<float>.Zero);

                // Ys = (Yd / alpha)^(1/gamma); E = D / (alpha x Ys^(gamma-1)) = (D / alpha) x (Yd / alpha)^((1-gamma)/gamma).
                var factor = Pow(yd, new Vector<float>((1f - HlgGamma) / HlgGamma)) * (1f / HlgAlpha);
                factor = Vector.ConditionalSelect(Vector.GreaterThan(yd, Vector<float>.Zero), factor, Vector<float>.Zero);
                (vr * factor).CopyTo(r[i..]);
                (vg * factor).CopyTo(g[i..]);
                (vb * factor).CopyTo(b[i..]);
            }
        }

        HlgInverseOotfScalar(r[i..], g[i..], b[i..]);
    }

    public static void HlgInverseOotfScalar(Span<float> r, Span<float> g, Span<float> b)
    {
        for (int i = 0; i < r.Length; i++)
        {
            double yd = Math.Max(((LumaR * r[i]) + (LumaG * g[i]) + (LumaB * b[i])) / HlgAlpha, 0.0);
            float factor = yd > 0 ? (float)(Math.Pow(yd, (1.0 - HlgGamma) / HlgGamma) / HlgAlpha) : 0f;
            r[i] *= factor;
            g[i] *= factor;
            b[i] *= factor;
        }
    }

    /// <summary>
    /// Extended Reinhard tone mapping on luminance, in place: a pixel of luminance <c>L</c> (reference-white units, BT.2100 weights) is scaled by
    /// <c>(1 + L/W^2) / (1 + L)</c>, which maps <c>L = W</c> to exactly 1 and leaves values near 0 almost untouched. Color ratios are kept, so hue does not shift.
    /// </summary>
    /// <param name="r">The red plane.</param>
    /// <param name="g">The green plane.</param>
    /// <param name="b">The blue plane.</param>
    /// <param name="whitePoint">The luminance <c>W</c> that maps to full SDR white: the content peak in reference-white units.</param>
    public static void ToneMapReinhard(Span<float> r, Span<float> g, Span<float> b, float whitePoint)
    {
        float inverseSquare = 1f / (whitePoint * whitePoint);
        int i = 0;
        if (Accelerated && r.Length >= Vector<float>.Count)
        {
            var k = new Vector<float>(inverseSquare);
            for (; i <= r.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var vr = new Vector<float>(r[i..]);
                var vg = new Vector<float>(g[i..]);
                var vb = new Vector<float>(b[i..]);
                var l = Vector.Max((vr * LumaR) + (vg * LumaG) + (vb * LumaB), Vector<float>.Zero);
                var scale = (Vector<float>.One + (l * k)) / (Vector<float>.One + l);
                (vr * scale).CopyTo(r[i..]);
                (vg * scale).CopyTo(g[i..]);
                (vb * scale).CopyTo(b[i..]);
            }
        }

        ToneMapReinhardScalar(r[i..], g[i..], b[i..], whitePoint);
    }

    public static void ToneMapReinhardScalar(Span<float> r, Span<float> g, Span<float> b, float whitePoint)
    {
        float inverseSquare = 1f / (whitePoint * whitePoint);
        for (int i = 0; i < r.Length; i++)
        {
            float l = Math.Max((LumaR * r[i]) + (LumaG * g[i]) + (LumaB * b[i]), 0f);
            float scale = (1f + (l * inverseSquare)) / (1f + l);
            r[i] *= scale;
            g[i] *= scale;
            b[i] *= scale;
        }
    }

    // ---------------------------------------------------------------- transfer functions

    /// <summary>Converts encoded samples to linear light in place.</summary>
    public static void ToLinear(TransferFunction transfer, double gamma, Span<float> v)
    {
        switch (transfer)
        {
            case TransferFunction.Linear:
                break;
            case TransferFunction.Srgb:
                Apply(v, Kind.SrgbDecode, 0f);
                break;
            case TransferFunction.Bt709:
                Apply(v, Kind.Bt709Decode, 0f);
                break;
            case TransferFunction.Gamma:
                Apply(v, Kind.Power, (float)gamma);
                break;
            case TransferFunction.Pq:
                Apply(v, Kind.PqDecode, 0f);
                break;
            case TransferFunction.Hlg:
                Apply(v, Kind.HlgDecode, 0f);
                break;
            default:
                throw new NotSupportedException($"The transfer function {transfer} cannot be converted.");
        }
    }

    /// <summary>Converts linear-light samples to the encoded form in place.</summary>
    public static void FromLinear(TransferFunction transfer, double gamma, Span<float> v)
    {
        switch (transfer)
        {
            case TransferFunction.Linear:
                break;
            case TransferFunction.Srgb:
                Apply(v, Kind.SrgbEncode, 0f);
                break;
            case TransferFunction.Bt709:
                Apply(v, Kind.Bt709Encode, 0f);
                break;
            case TransferFunction.Gamma:
                Apply(v, Kind.Power, (float)(1.0 / gamma));
                break;
            case TransferFunction.Pq:
                Apply(v, Kind.PqEncode, 0f);
                break;
            case TransferFunction.Hlg:
                Apply(v, Kind.HlgEncode, 0f);
                break;
            default:
                throw new NotSupportedException($"The transfer function {transfer} cannot be converted.");
        }
    }

    internal enum Kind
    {
        SrgbDecode,
        SrgbEncode,
        Bt709Decode,
        Bt709Encode,
        Power,
        PqDecode,
        PqEncode,
        HlgDecode,
        HlgEncode,
    }

    internal static void Apply(Span<float> v, Kind kind, float parameter)
    {
        int i = 0;
        if (Accelerated && v.Length >= Vector<float>.Count)
        {
            for (; i <= v.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var x = new Vector<float>(v[i..]);
                var sign = x & SignMask;
                var magnitude = Vector.Abs(x);
                var y = kind switch
                {
                    Kind.SrgbDecode => SrgbDecode(magnitude),
                    Kind.SrgbEncode => SrgbEncode(magnitude),
                    Kind.Bt709Decode => Bt709Decode(magnitude),
                    Kind.Bt709Encode => Bt709Encode(magnitude),
                    Kind.Power => Pow(magnitude, new Vector<float>(parameter)),
                    Kind.PqDecode => PqDecode(magnitude),
                    Kind.PqEncode => PqEncode(magnitude),
                    Kind.HlgDecode => HlgDecode(magnitude),
                    _ => HlgEncode(magnitude),
                };
                (y | sign).CopyTo(v[i..]);
            }
        }

        ApplyScalar(v[i..], kind, parameter);
    }

    /// <summary>The double-precision reference every vector kernel is compared against.</summary>
    internal static void ApplyScalar(Span<float> v, Kind kind, float parameter)
    {
        for (int i = 0; i < v.Length; i++)
        {
            double x = v[i];
            double m = Math.Abs(x);
            double y = kind switch
            {
                Kind.SrgbDecode => NamedSpaces.SrgbToLinear(m),
                Kind.SrgbEncode => NamedSpaces.LinearToSrgb(m),
                Kind.Bt709Decode => NamedSpaces.Bt709ToLinear(m),
                Kind.Bt709Encode => NamedSpaces.LinearToBt709(m),
                Kind.Power => Math.Pow(m, parameter),
                Kind.PqDecode => HdrDecode(m, pq: true),
                Kind.PqEncode => HdrEncode(m, pq: true),
                Kind.HlgDecode => HdrDecode(m, pq: false),
                _ => HdrEncode(m, pq: false),
            };
            v[i] = (float)(x < 0 ? -y : y);
        }
    }

    // PQ and HLG are defined on 0..1 signals; an out-of-range signal clamps (their domain has no extension).
    internal static double HdrDecode(double m, bool pq) => pq ? Hdr.PqToLinear(m) : Hdr.HlgToLinear(m);

    internal static double HdrEncode(double m, bool pq) => pq ? Hdr.LinearToPq(m) : Hdr.LinearToHlg(m);

    // ---------------------------------------------------------------- vector math

    internal static Vector<float> SrgbDecode(Vector<float> e)
    {
        var low = e * (1f / 12.92f);
        var high = Pow((e + new Vector<float>(0.055f)) * (1f / 1.055f), new Vector<float>(2.4f));
        return Vector.ConditionalSelect(Vector.LessThanOrEqual(e, new Vector<float>(0.04045f)), low, high);
    }

    internal static Vector<float> SrgbEncode(Vector<float> l)
    {
        var low = l * 12.92f;
        var high = (Pow(l, new Vector<float>(1f / 2.4f)) * 1.055f) - new Vector<float>(0.055f);
        return Vector.ConditionalSelect(Vector.LessThanOrEqual(l, new Vector<float>(0.0031308f)), low, high);
    }

    internal static Vector<float> Bt709Decode(Vector<float> e)
    {
        var low = e * (1f / 4.5f);
        var high = Pow((e + new Vector<float>(0.099f)) * (1f / 1.099f), new Vector<float>(1f / 0.45f));
        return Vector.ConditionalSelect(Vector.LessThan(e, new Vector<float>(0.081f)), low, high);
    }

    internal static Vector<float> Bt709Encode(Vector<float> l)
    {
        var low = l * 4.5f;
        var high = (Pow(l, new Vector<float>(0.45f)) * 1.099f) - new Vector<float>(0.099f);
        return Vector.ConditionalSelect(Vector.LessThan(l, new Vector<float>(0.018f)), low, high);
    }

    internal static Vector<float> PqDecode(Vector<float> e)
    {
        const float M1 = 2610f / 16384f, M2 = 2523f / 4096f * 128f, C1 = 3424f / 4096f, C2 = 2413f / 4096f * 32f, C3 = 2392f / 4096f * 32f;
        var p = Pow(Vector.Min(e, Vector<float>.One), new Vector<float>(1f / M2));
        var num = Vector.Max(p - new Vector<float>(C1), Vector<float>.Zero);
        var den = new Vector<float>(C2) - (p * C3);
        var y = Pow(num / den, new Vector<float>(1f / M1));
        return y * (float)(Hdr.PqPeakNits / Hdr.ReferenceWhiteNits);
    }

    internal static Vector<float> PqEncode(Vector<float> l)
    {
        const float M1 = 2610f / 16384f, M2 = 2523f / 4096f * 128f, C1 = 3424f / 4096f, C2 = 2413f / 4096f * 32f, C3 = 2392f / 4096f * 32f;
        var y = Vector.Min(l * (float)(Hdr.ReferenceWhiteNits / Hdr.PqPeakNits), Vector<float>.One);
        var ym = Pow(y, new Vector<float>(M1));
        return Pow((new Vector<float>(C1) + (ym * C2)) / (Vector<float>.One + (ym * C3)), new Vector<float>(M2));
    }

    private const float HlgA = 0.17883277f;
    private const float HlgB = 1f - (4f * HlgA);
    private const float HlgC = 0.55991073f; // 0.5 - a * ln(4a)

    internal static Vector<float> HlgDecode(Vector<float> e)
    {
        e = Vector.Min(e, Vector<float>.One);
        var low = e * e * (1f / 3f);
        var high = (Exp((e - new Vector<float>(HlgC)) * (1f / HlgA)) + new Vector<float>(HlgB)) * (1f / 12f);
        var scene = Vector.ConditionalSelect(Vector.LessThanOrEqual(e, new Vector<float>(0.5f)), low, high);
        return scene;
    }

    internal static Vector<float> HlgEncode(Vector<float> l)
    {
        var s = Vector.Min(l, Vector<float>.One);
        var low = Vector.SquareRoot(s * 3f);
        var logArg = Vector.Max((s * 12f) - new Vector<float>(HlgB), new Vector<float>(1e-30f));
        var high = (Log(logArg) * HlgA) + new Vector<float>(HlgC);
        return Vector.ConditionalSelect(Vector.LessThanOrEqual(s, new Vector<float>(1f / 12f)), low, high);
    }

    /// <summary>x^y for x &gt;= 0 (0 stays 0).</summary>
    internal static Vector<float> Pow(Vector<float> x, Vector<float> y)
    {
        var positive = Vector.GreaterThan(x, Vector<float>.Zero);
        var result = Exp2(y * Log2(Vector.Max(x, new Vector<float>(1.17549435e-38f))));
        return Vector.ConditionalSelect(positive, result, Vector<float>.Zero);
    }

    private static Vector<float> Exp(Vector<float> x) => Exp2(x * 1.44269504f);

    private static Vector<float> Log(Vector<float> x) => Log2(x) * 0.693147181f;

    private static Vector<float> Exp2(Vector<float> x)
    {
        x = Vector.Min(Vector.Max(x, new Vector<float>(-126f)), new Vector<float>(126f));
        var n = Vector.Round(x);
        var f = x - n;

        // Degree-6 Taylor series of 2^f on [-0.5, 0.5]: error below 1.3e-7.
        var p = new Vector<float>(0.000154035304f);
        p = (p * f) + new Vector<float>(0.00133335581f);
        p = (p * f) + new Vector<float>(0.00961812911f);
        p = (p * f) + new Vector<float>(0.0555041087f);
        p = (p * f) + new Vector<float>(0.240226507f);
        p = (p * f) + new Vector<float>(0.693147181f);
        p = (p * f) + Vector<float>.One;

        var scale = Vector.AsVectorSingle(Vector.ShiftLeft(Vector.ConvertToInt32(n) + new Vector<int>(127), 23));
        return p * scale;
    }

    private static Vector<float> Log2(Vector<float> x)
    {
        var bits = Vector.AsVectorInt32(x);
        var exponent = Vector.ConvertToSingle(Vector.ShiftRightLogical(bits, 23) - new Vector<int>(127));
        var m = Vector.AsVectorSingle((bits & new Vector<int>(0x007FFFFF)) | new Vector<int>(0x3F800000));

        // Keep the mantissa in [sqrt(1/2), sqrt(2)) so the series below converges fast.
        var big = Vector.GreaterThan(m, new Vector<float>(1.41421356f));
        m = Vector.ConditionalSelect(big, m * 0.5f, m);
        exponent = Vector.ConditionalSelect(big, exponent + Vector<float>.One, exponent);

        var s = (m - Vector<float>.One) / (m + Vector<float>.One);
        var s2 = s * s;
        var series = new Vector<float>(1f / 9f);
        series = (series * s2) + new Vector<float>(1f / 7f);
        series = (series * s2) + new Vector<float>(1f / 5f);
        series = (series * s2) + new Vector<float>(1f / 3f);
        series = (series * s2) + Vector<float>.One;
        return exponent + (s * series * 2.88539008f); // 2 / ln(2)
    }
}
