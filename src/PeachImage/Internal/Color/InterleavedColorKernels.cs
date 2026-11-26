using System.Numerics;
using System.Runtime.Intrinsics;

namespace PeachImage.Internal.Color;

/// <summary>
/// The color kernels over interleaved float RGBA rows (r, g, b, a, r, g, b, a, ...) with no transposition to planes: the transfer functions
/// and clamp run on <see cref="Vector{T}"/> across the flat row and put the alpha lanes back afterwards; the 3x3 matrix uses
/// <see cref="Vector256{T}"/> (two pixels per vector, AVX2) or <see cref="Vector128{T}"/> (one pixel, SSE and ARM NEON) shuffles. Alpha is never changed.
/// Each has a scalar twin that the tests compare against.
/// </summary>
internal static class InterleavedColorKernels
{
    private static readonly Vector<int> AlphaLanes = BuildAlphaLanes();

    private static Vector<int> BuildAlphaLanes()
    {
        var lanes = new int[Vector<int>.Count];
        for (int i = 0; i < lanes.Length; i++)
        {
            lanes[i] = i % 4 == 3 ? -1 : 0;
        }

        return new Vector<int>(lanes);
    }

    /// <summary>Applies a transfer function to the color samples of <paramref name="rgba"/> (a whole number of pixels), in place.</summary>
    public static void Transfer(Span<float> rgba, ColorKernels.Kind kind, float parameter)
    {
        int i = 0;
        if (ColorKernels.Accelerated && rgba.Length >= Vector<float>.Count)
        {
            var alpha = Vector.AsVectorSingle(AlphaLanes);
            for (; i <= rgba.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var x = new Vector<float>(rgba[i..]);
                var sign = x & ColorKernels.SignMask;
                var magnitude = Vector.Abs(x);
                var y = kind switch
                {
                    ColorKernels.Kind.SrgbDecode => ColorKernels.SrgbDecode(magnitude),
                    ColorKernels.Kind.SrgbEncode => ColorKernels.SrgbEncode(magnitude),
                    ColorKernels.Kind.Bt709Decode => ColorKernels.Bt709Decode(magnitude),
                    ColorKernels.Kind.Bt709Encode => ColorKernels.Bt709Encode(magnitude),
                    ColorKernels.Kind.Power => ColorKernels.Pow(magnitude, new Vector<float>(parameter)),
                    ColorKernels.Kind.PqDecode => ColorKernels.PqDecode(magnitude),
                    ColorKernels.Kind.PqEncode => ColorKernels.PqEncode(magnitude),
                    ColorKernels.Kind.HlgDecode => ColorKernels.HlgDecode(magnitude),
                    _ => ColorKernels.HlgEncode(magnitude),
                };
                Vector.ConditionalSelect(alpha, x, y | sign).CopyTo(rgba[i..]);
            }
        }

        TransferScalar(rgba[i..], kind, parameter);
    }

    public static void TransferScalar(Span<float> rgba, ColorKernels.Kind kind, float parameter)
    {
        for (int i = 0; i < rgba.Length; i++)
        {
            if (i % 4 == 3)
            {
                continue;
            }

            double x = rgba[i];
            double m = Math.Abs(x);
            double y = kind switch
            {
                ColorKernels.Kind.SrgbDecode => NamedSpaces.SrgbToLinear(m),
                ColorKernels.Kind.SrgbEncode => NamedSpaces.LinearToSrgb(m),
                ColorKernels.Kind.Bt709Decode => NamedSpaces.Bt709ToLinear(m),
                ColorKernels.Kind.Bt709Encode => NamedSpaces.LinearToBt709(m),
                ColorKernels.Kind.Power => Math.Pow(m, parameter),
                ColorKernels.Kind.PqDecode => ColorKernels.HdrDecode(m, pq: true),
                ColorKernels.Kind.PqEncode => ColorKernels.HdrEncode(m, pq: true),
                ColorKernels.Kind.HlgDecode => ColorKernels.HdrDecode(m, pq: false),
                _ => ColorKernels.HdrEncode(m, pq: false),
            };
            rgba[i] = (float)(x < 0 ? -y : y);
        }
    }

    /// <summary>Converts encoded color samples to linear light in place (alpha untouched).</summary>
    public static void ToLinear(TransferFunction transfer, double gamma, Span<float> rgba)
    {
        switch (transfer)
        {
            case TransferFunction.Linear:
                break;
            case TransferFunction.Srgb:
                Transfer(rgba, ColorKernels.Kind.SrgbDecode, 0f);
                break;
            case TransferFunction.Bt709:
                Transfer(rgba, ColorKernels.Kind.Bt709Decode, 0f);
                break;
            case TransferFunction.Gamma:
                Transfer(rgba, ColorKernels.Kind.Power, (float)gamma);
                break;
            case TransferFunction.Pq:
                Transfer(rgba, ColorKernels.Kind.PqDecode, 0f);
                break;
            case TransferFunction.Hlg:
                Transfer(rgba, ColorKernels.Kind.HlgDecode, 0f);
                break;
            default:
                throw new NotSupportedException($"The transfer function {transfer} cannot be converted.");
        }
    }

    /// <summary>Converts linear-light color samples to the encoded form in place (alpha untouched).</summary>
    public static void FromLinear(TransferFunction transfer, double gamma, Span<float> rgba)
    {
        switch (transfer)
        {
            case TransferFunction.Linear:
                break;
            case TransferFunction.Srgb:
                Transfer(rgba, ColorKernels.Kind.SrgbEncode, 0f);
                break;
            case TransferFunction.Bt709:
                Transfer(rgba, ColorKernels.Kind.Bt709Encode, 0f);
                break;
            case TransferFunction.Gamma:
                Transfer(rgba, ColorKernels.Kind.Power, (float)(1.0 / gamma));
                break;
            case TransferFunction.Pq:
                Transfer(rgba, ColorKernels.Kind.PqEncode, 0f);
                break;
            case TransferFunction.Hlg:
                Transfer(rgba, ColorKernels.Kind.HlgEncode, 0f);
                break;
            default:
                throw new NotSupportedException($"The transfer function {transfer} cannot be converted.");
        }
    }

    /// <summary>Clamps the color samples to 0..1 (NaN becomes 0), in place.</summary>
    public static void ClampUnit(Span<float> rgba)
    {
        int i = 0;
        if (ColorKernels.Accelerated && rgba.Length >= Vector<float>.Count)
        {
            var alpha = Vector.AsVectorSingle(AlphaLanes);
            for (; i <= rgba.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var x = new Vector<float>(rgba[i..]);
                var valid = Vector.Equals(x, x);
                var clamped = Vector.Min(Vector.Max(Vector.ConditionalSelect(valid, x, Vector<float>.Zero), Vector<float>.Zero), Vector<float>.One);
                Vector.ConditionalSelect(alpha, x, clamped).CopyTo(rgba[i..]);
            }
        }

        ClampUnitScalar(rgba[i..]);
    }

    public static void ClampUnitScalar(Span<float> rgba)
    {
        for (int i = 0; i < rgba.Length; i++)
        {
            if (i % 4 == 3)
            {
                continue;
            }

            float x = rgba[i];
            rgba[i] = x > 0f ? (x < 1f ? x : 1f) : 0f;
        }
    }

    /// <summary>Multiplies the (r, g, b) of each of <paramref name="pixels"/> pixels by the row-major 3x3 <paramref name="m"/>, in place.</summary>
    public static void Matrix3x3(Span<float> rgba, int pixels, ReadOnlySpan<float> m)
    {
        int done = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            done = Matrix3x3Vector256(rgba, pixels, m);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            done = Matrix3x3Vector128(rgba, pixels, m);
        }

        Matrix3x3Scalar(rgba[(done * 4)..], pixels - done, m);
    }

    internal static int Matrix3x3Vector128(Span<float> rgba, int pixels, ReadOnlySpan<float> m)
    {
        var c0 = Vector128.Create(m[0], m[3], m[6], 0f);
        var c1 = Vector128.Create(m[1], m[4], m[7], 0f);
        var c2 = Vector128.Create(m[2], m[5], m[8], 0f);
        var alphaLane = Vector128.Create(0, 0, 0, -1).AsSingle();
        var rr = Vector128.Create(0, 0, 0, 0);
        var gg = Vector128.Create(1, 1, 1, 1);
        var bb = Vector128.Create(2, 2, 2, 2);
        int i = 0;
        for (; i < pixels; i++)
        {
            var v = Vector128.Create<float>(rgba.Slice(i * 4, 4));
            var mixed = (Vector128.Shuffle(v, rr) * c0) + (Vector128.Shuffle(v, gg) * c1) + (Vector128.Shuffle(v, bb) * c2);
            Vector128.ConditionalSelect(alphaLane, v, mixed).CopyTo(rgba.Slice(i * 4, 4));
        }

        return i;
    }

    internal static int Matrix3x3Vector256(Span<float> rgba, int pixels, ReadOnlySpan<float> m)
    {
        var c0 = Vector256.Create(m[0], m[3], m[6], 0f, m[0], m[3], m[6], 0f);
        var c1 = Vector256.Create(m[1], m[4], m[7], 0f, m[1], m[4], m[7], 0f);
        var c2 = Vector256.Create(m[2], m[5], m[8], 0f, m[2], m[5], m[8], 0f);
        var alphaLane = Vector256.Create(0, 0, 0, -1, 0, 0, 0, -1).AsSingle();
        var rr = Vector256.Create(0, 0, 0, 0, 4, 4, 4, 4);
        var gg = Vector256.Create(1, 1, 1, 1, 5, 5, 5, 5);
        var bb = Vector256.Create(2, 2, 2, 2, 6, 6, 6, 6);
        int i = 0;
        for (; i <= pixels - 2; i += 2)
        {
            var v = Vector256.Create<float>(rgba.Slice(i * 4, 8));
            var mixed = (Vector256.Shuffle(v, rr) * c0) + (Vector256.Shuffle(v, gg) * c1) + (Vector256.Shuffle(v, bb) * c2);
            Vector256.ConditionalSelect(alphaLane, v, mixed).CopyTo(rgba.Slice(i * 4, 8));
        }

        return i;
    }

    public static void Matrix3x3Scalar(Span<float> rgba, int pixels, ReadOnlySpan<float> m)
    {
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            float r = rgba[o], g = rgba[o + 1], b = rgba[o + 2];
            rgba[o] = (r * m[0]) + (g * m[1]) + (b * m[2]);
            rgba[o + 1] = (r * m[3]) + (g * m[4]) + (b * m[5]);
            rgba[o + 2] = (r * m[6]) + (g * m[7]) + (b * m[8]);
        }
    }
}
