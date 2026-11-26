using System.Numerics;
using System.Runtime.Intrinsics;

namespace PeachImage.Internal.PixelFormatConversion;

/// <summary>
/// Kernels over interleaved float RGBA rows (the converter's working layout) and over half-precision rows. Premultiplying uses
/// <see cref="Vector128{T}"/> (one pixel per vector) and <see cref="Vector256{T}"/> (two) so it covers AVX2 and ARM NEON from one source; the
/// half conversions use <see cref="Vector{T}"/>. Each has a scalar twin the tests compare against, and the scalar code also finishes every tail.
/// </summary>
internal static class RgbaRowKernels
{
    // ------------------------------------------------------------ premultiply (interleaved r, g, b, a)

    /// <summary>Multiplies color by alpha for <paramref name="pixels"/> interleaved float RGBA pixels, in place.</summary>
    public static void Premultiply(Span<float> rgba, int pixels)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            i = PremultiplyVector256(rgba, pixels);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            i = PremultiplyVector128(rgba, pixels);
        }

        PremultiplyScalar(rgba[(i * 4)..], pixels - i);
    }

    /// <summary>Divides color by alpha for <paramref name="pixels"/> interleaved float RGBA pixels, in place; alpha 0 gives color 0.</summary>
    public static void Unpremultiply(Span<float> rgba, int pixels)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            i = UnpremultiplyVector256(rgba, pixels);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            i = UnpremultiplyVector128(rgba, pixels);
        }

        UnpremultiplyScalar(rgba[(i * 4)..], pixels - i);
    }

    internal static int PremultiplyVector128(Span<float> rgba, int pixels)
    {
        var alphaLane = Vector128.Create(0, 0, 0, -1).AsSingle();
        var broadcast = Vector128.Create(3, 3, 3, 3);
        int i = 0;
        for (; i < pixels; i++)
        {
            var v = Vector128.Create<float>(rgba.Slice(i * 4, 4));
            var scaled = v * Vector128.Shuffle(v, broadcast);
            Vector128.ConditionalSelect(alphaLane, v, scaled).CopyTo(rgba.Slice(i * 4, 4));
        }

        return i;
    }

    internal static int PremultiplyVector256(Span<float> rgba, int pixels)
    {
        var alphaLane = Vector256.Create(0, 0, 0, -1, 0, 0, 0, -1).AsSingle();
        var broadcast = Vector256.Create(3, 3, 3, 3, 7, 7, 7, 7);
        int i = 0;
        for (; i <= pixels - 2; i += 2)
        {
            var v = Vector256.Create<float>(rgba.Slice(i * 4, 8));
            var scaled = v * Vector256.Shuffle(v, broadcast);
            Vector256.ConditionalSelect(alphaLane, v, scaled).CopyTo(rgba.Slice(i * 4, 8));
        }

        return i;
    }

    internal static int UnpremultiplyVector128(Span<float> rgba, int pixels)
    {
        var alphaLane = Vector128.Create(0, 0, 0, -1).AsSingle();
        var broadcast = Vector128.Create(3, 3, 3, 3);
        int i = 0;
        for (; i < pixels; i++)
        {
            var v = Vector128.Create<float>(rgba.Slice(i * 4, 4));
            var a = Vector128.Shuffle(v, broadcast);
            var inv = Vector128.ConditionalSelect(Vector128.GreaterThan(a, Vector128<float>.Zero), Vector128<float>.One / a, Vector128<float>.Zero);
            Vector128.ConditionalSelect(alphaLane, v, v * inv).CopyTo(rgba.Slice(i * 4, 4));
        }

        return i;
    }

    internal static int UnpremultiplyVector256(Span<float> rgba, int pixels)
    {
        var alphaLane = Vector256.Create(0, 0, 0, -1, 0, 0, 0, -1).AsSingle();
        var broadcast = Vector256.Create(3, 3, 3, 3, 7, 7, 7, 7);
        int i = 0;
        for (; i <= pixels - 2; i += 2)
        {
            var v = Vector256.Create<float>(rgba.Slice(i * 4, 8));
            var a = Vector256.Shuffle(v, broadcast);
            var inv = Vector256.ConditionalSelect(Vector256.GreaterThan(a, Vector256<float>.Zero), Vector256<float>.One / a, Vector256<float>.Zero);
            Vector256.ConditionalSelect(alphaLane, v, v * inv).CopyTo(rgba.Slice(i * 4, 8));
        }

        return i;
    }

    internal static void PremultiplyScalar(Span<float> rgba, int pixels)
    {
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            float a = rgba[o + 3];
            rgba[o] *= a;
            rgba[o + 1] *= a;
            rgba[o + 2] *= a;
        }
    }

    internal static void UnpremultiplyScalar(Span<float> rgba, int pixels)
    {
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            float a = rgba[o + 3];
            float inv = a > 0f ? 1f / a : 0f;
            rgba[o] *= inv;
            rgba[o + 1] *= inv;
            rgba[o + 2] *= inv;
        }
    }

    // ------------------------------------------------------------ planar <-> interleaved

    /// <summary>Splits interleaved RGBA into four planes.</summary>
    public static void Deinterleave(ReadOnlySpan<float> rgba, Span<float> r, Span<float> g, Span<float> b, Span<float> a, int pixels)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            i = DeinterleaveVector128(rgba, r, g, b, a, pixels);
        }

        DeinterleaveScalar(rgba[(i * 4)..], r[i..], g[i..], b[i..], a[i..], pixels - i);
    }

    /// <summary>Joins four planes into interleaved RGBA.</summary>
    public static void Interleave(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> a, Span<float> rgba, int pixels)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            i = InterleaveVector128(r, g, b, a, rgba, pixels);
        }

        InterleaveScalar(r[i..], g[i..], b[i..], a[i..], rgba[(i * 4)..], pixels - i);
    }

    // A 4x4 transpose built from single-source shuffles and lane selects, so one body serves SSE, AVX2 hosts and ARM NEON alike.
    private static readonly Vector128<int> Lane0 = Vector128.Create(-1, 0, 0, 0);
    private static readonly Vector128<int> Lane1 = Vector128.Create(0, -1, 0, 0);
    private static readonly Vector128<int> Lane2 = Vector128.Create(0, 0, -1, 0);

    private static Vector128<float> Pick(Vector128<float> p0, Vector128<float> p1, Vector128<float> p2, Vector128<float> p3, Vector128<int> index)
    {
        var from0 = Vector128.Shuffle(p0, index);
        var from1 = Vector128.Shuffle(p1, index);
        var from2 = Vector128.Shuffle(p2, index);
        var from3 = Vector128.Shuffle(p3, index);
        return Vector128.ConditionalSelect(Lane0.AsSingle(), from0, Vector128.ConditionalSelect(Lane1.AsSingle(), from1, Vector128.ConditionalSelect(Lane2.AsSingle(), from2, from3)));
    }

    internal static int DeinterleaveVector128(ReadOnlySpan<float> rgba, Span<float> r, Span<float> g, Span<float> b, Span<float> a, int pixels)
    {
        var c0 = Vector128.Create(0, 0, 0, 0);
        var c1 = Vector128.Create(1, 1, 1, 1);
        var c2 = Vector128.Create(2, 2, 2, 2);
        var c3 = Vector128.Create(3, 3, 3, 3);
        int i = 0;
        for (; i <= pixels - 4; i += 4)
        {
            var p0 = Vector128.Create(rgba.Slice(i * 4, 4));
            var p1 = Vector128.Create(rgba.Slice((i * 4) + 4, 4));
            var p2 = Vector128.Create(rgba.Slice((i * 4) + 8, 4));
            var p3 = Vector128.Create(rgba.Slice((i * 4) + 12, 4));
            Pick(p0, p1, p2, p3, c0).CopyTo(r.Slice(i, 4));
            Pick(p0, p1, p2, p3, c1).CopyTo(g.Slice(i, 4));
            Pick(p0, p1, p2, p3, c2).CopyTo(b.Slice(i, 4));
            Pick(p0, p1, p2, p3, c3).CopyTo(a.Slice(i, 4));
        }

        return i;
    }

    internal static int InterleaveVector128(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> a, Span<float> rgba, int pixels)
    {
        var p0 = Vector128.Create(0, 0, 0, 0);
        var p1 = Vector128.Create(1, 1, 1, 1);
        var p2 = Vector128.Create(2, 2, 2, 2);
        var p3 = Vector128.Create(3, 3, 3, 3);
        int i = 0;
        for (; i <= pixels - 4; i += 4)
        {
            var vr = Vector128.Create(r.Slice(i, 4));
            var vg = Vector128.Create(g.Slice(i, 4));
            var vb = Vector128.Create(b.Slice(i, 4));
            var va = Vector128.Create(a.Slice(i, 4));
            Pick(vr, vg, vb, va, p0).CopyTo(rgba.Slice(i * 4, 4));
            Pick(vr, vg, vb, va, p1).CopyTo(rgba.Slice((i * 4) + 4, 4));
            Pick(vr, vg, vb, va, p2).CopyTo(rgba.Slice((i * 4) + 8, 4));
            Pick(vr, vg, vb, va, p3).CopyTo(rgba.Slice((i * 4) + 12, 4));
        }

        return i;
    }

    internal static void DeinterleaveScalar(ReadOnlySpan<float> rgba, Span<float> r, Span<float> g, Span<float> b, Span<float> a, int pixels)
    {
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            r[i] = rgba[o];
            g[i] = rgba[o + 1];
            b[i] = rgba[o + 2];
            a[i] = rgba[o + 3];
        }
    }

    internal static void InterleaveScalar(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> a, Span<float> rgba, int pixels)
    {
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            rgba[o] = r[i];
            rgba[o + 1] = g[i];
            rgba[o + 2] = b[i];
            rgba[o + 3] = a[i];
        }
    }

    // ------------------------------------------------------------ flat integer <-> float (RGBA formats need no shuffling)

    private const float Inv255 = 1f / 255f;
    private const float Inv65535 = 1f / 65535f;

    /// <summary>Converts 8-bit samples to float in 0..1.</summary>
    public static void BytesToSingle(ReadOnlySpan<byte> source, Span<float> destination)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int n = Vector<byte>.Count;
            var scale = new Vector<float>(Inv255);
            for (; i <= source.Length - n; i += n)
            {
                Vector.Widen(new Vector<byte>(source[i..]), out var lo16, out var hi16);
                Vector.Widen(lo16, out var a, out var b);
                Vector.Widen(hi16, out var c, out var d);
                int k = Vector<float>.Count;
                (Vector.ConvertToSingle(a) * scale).CopyTo(destination[i..]);
                (Vector.ConvertToSingle(b) * scale).CopyTo(destination[(i + k)..]);
                (Vector.ConvertToSingle(c) * scale).CopyTo(destination[(i + (2 * k))..]);
                (Vector.ConvertToSingle(d) * scale).CopyTo(destination[(i + (3 * k))..]);
            }
        }

        BytesToSingleScalar(source[i..], destination[i..]);
    }

    /// <summary>Converts float samples to 8-bit: round to nearest (ties up), clip to the range, NaN to 0.</summary>
    public static void SingleToBytes(ReadOnlySpan<float> source, Span<byte> destination)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int n = Vector<byte>.Count;
            int k = Vector<float>.Count;
            for (; i <= source.Length - n; i += n)
            {
                var a = ToUInt(new Vector<float>(source[i..]), 255f);
                var b = ToUInt(new Vector<float>(source[(i + k)..]), 255f);
                var c = ToUInt(new Vector<float>(source[(i + (2 * k))..]), 255f);
                var d = ToUInt(new Vector<float>(source[(i + (3 * k))..]), 255f);
                Vector.Narrow(Vector.Narrow(a, b), Vector.Narrow(c, d)).CopyTo(destination[i..]);
            }
        }

        SingleToBytesScalar(source[i..], destination[i..]);
    }

    /// <summary>Converts 16-bit samples to float in 0..1.</summary>
    public static void UShortsToSingle(ReadOnlySpan<ushort> source, Span<float> destination)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int n = Vector<ushort>.Count;
            var scale = new Vector<float>(Inv65535);
            for (; i <= source.Length - n; i += n)
            {
                Vector.Widen(new Vector<ushort>(source[i..]), out var lo, out var hi);
                (Vector.ConvertToSingle(lo) * scale).CopyTo(destination[i..]);
                (Vector.ConvertToSingle(hi) * scale).CopyTo(destination[(i + Vector<float>.Count)..]);
            }
        }

        UShortsToSingleScalar(source[i..], destination[i..]);
    }

    /// <summary>Converts float samples to 16-bit: round to nearest (ties up), clip to the range, NaN to 0.</summary>
    public static void SingleToUShorts(ReadOnlySpan<float> source, Span<ushort> destination)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int n = Vector<ushort>.Count;
            int k = Vector<float>.Count;
            for (; i <= source.Length - n; i += n)
            {
                var a = ToUInt(new Vector<float>(source[i..]), 65535f);
                var b = ToUInt(new Vector<float>(source[(i + k)..]), 65535f);
                Vector.Narrow(a, b).CopyTo(destination[i..]);
            }
        }

        SingleToUShortsScalar(source[i..], destination[i..]);
    }

    // value > 0 ? (uint)(min(value, 1) * max + 0.5) : 0, exactly the scalar expression (NaN fails the comparison and gives 0).
    private static Vector<uint> ToUInt(Vector<float> value, float max)
    {
        var positive = Vector.GreaterThan(value, Vector<float>.Zero);
        var scaled = (Vector.Min(value, Vector<float>.One) * max) + new Vector<float>(0.5f);
        return Vector.ConvertToUInt32(Vector.ConditionalSelect(positive, scaled, Vector<float>.Zero));
    }

    internal static void BytesToSingleScalar(ReadOnlySpan<byte> source, Span<float> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = source[i] * Inv255;
        }
    }

    internal static void SingleToBytesScalar(ReadOnlySpan<float> source, Span<byte> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            float value = source[i];
            destination[i] = value > 0f ? (byte)((Math.Min(value, 1f) * 255f) + 0.5f) : (byte)0;
        }
    }

    internal static void UShortsToSingleScalar(ReadOnlySpan<ushort> source, Span<float> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = source[i] * Inv65535;
        }
    }

    internal static void SingleToUShortsScalar(ReadOnlySpan<float> source, Span<ushort> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            float value = source[i];
            destination[i] = value > 0f ? (ushort)((Math.Min(value, 1f) * 65535f) + 0.5f) : (ushort)0;
        }
    }

    // ------------------------------------------------------------ half precision

    /// <summary>Widens half-precision samples to float (exact).</summary>
    public static void HalfToSingle(ReadOnlySpan<Half> source, Span<float> destination)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            i = HalfToSingleVector(source, destination);
        }

        HalfToSingleScalar(source[i..], destination[i..]);
    }

    /// <summary>Narrows float samples to half precision with round-to-nearest-even; magnitudes above 65504 saturate to 65504 and NaN becomes 0.</summary>
    public static void SingleToHalf(ReadOnlySpan<float> source, Span<Half> destination)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            i = SingleToHalfVector(source, destination);
        }

        SingleToHalfScalar(source[i..], destination[i..]);
    }

    internal static void HalfToSingleScalar(ReadOnlySpan<Half> source, Span<float> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = (float)source[i];
        }
    }

    internal static void SingleToHalfScalar(ReadOnlySpan<float> source, Span<Half> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            float v = source[i];
            destination[i] = float.IsNaN(v) ? Half.Zero : (Half)Math.Clamp(v, -65504f, 65504f);
        }
    }

    internal static int HalfToSingleVector(ReadOnlySpan<Half> source, Span<float> destination)
    {
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<Half, ushort>(source);
        int n = Vector<ushort>.Count;
        int i = 0;
        var magic = new Vector<float>(BitConverter.UInt32BitsToSingle(0x77800000u));
        var infNan = new Vector<float>(65536f);
        for (; i <= source.Length - n; i += n)
        {
            Vector.Widen(new Vector<ushort>(halves[i..]), out var lo, out var hi);
            Widen(lo, magic, infNan).CopyTo(destination[i..]);
            Widen(hi, magic, infNan).CopyTo(destination[(i + Vector<uint>.Count)..]);
        }

        return i;

        static Vector<float> Widen(Vector<uint> h, Vector<float> magic, Vector<float> infNan)
        {
            var exponentMantissa = Vector.ShiftLeft(h & new Vector<uint>(0x7FFFu), 13);
            var o = Vector.AsVectorSingle(exponentMantissa) * magic;
            var special = Vector.GreaterThanOrEqual(o, infNan);
            var bits = Vector.AsVectorUInt32(o);
            bits = Vector.ConditionalSelect(Vector.AsVectorUInt32(special), bits | new Vector<uint>(0x7F800000u), bits);
            return Vector.AsVectorSingle(bits | Vector.ShiftLeft(h & new Vector<uint>(0x8000u), 16));
        }
    }

    internal static int SingleToHalfVector(ReadOnlySpan<float> source, Span<Half> destination)
    {
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<Half, ushort>(destination);
        int n = Vector<float>.Count;
        int i = 0;
        var max = new Vector<float>(65504f);
        var min = new Vector<float>(-65504f);
        for (; i <= source.Length - 2 * n; i += 2 * n)
        {
            var lo = Narrow(new Vector<float>(source[i..]), min, max);
            var hi = Narrow(new Vector<float>(source[(i + n)..]), min, max);
            Vector.Narrow(lo, hi).CopyTo(halves[i..]);
        }

        return i;

        static Vector<uint> Narrow(Vector<float> value, Vector<float> min, Vector<float> max)
        {
            // NaN lanes become 0, finite lanes clamp to the half range, then the bit manipulation rounds to nearest even.
            var valid = Vector.Equals(value, value);
            value = Vector.Min(Vector.Max(Vector.ConditionalSelect(valid, value, Vector<float>.Zero), min), max);
            var bits = Vector.AsVectorUInt32(value);
            var sign = bits & new Vector<uint>(0x80000000u);
            bits ^= sign;
            var magnitude = Vector.AsVectorSingle(bits);

            // Subnormal and zero results: adding a magic number shifts the mantissa into place with correct rounding.
            var denormMagic = new Vector<float>(BitConverter.UInt32BitsToSingle(126u << 23));
            var subnormal = Vector.AsVectorUInt32(magnitude + denormMagic) - new Vector<uint>(126u << 23);

            // Normal results: rebias the exponent, add the round-to-even bias, then drop 13 mantissa bits.
            var mantOdd = Vector.ShiftRightLogical(bits, 13) & new Vector<uint>(1u);
            var normal = Vector.ShiftRightLogical(bits + new Vector<uint>(unchecked(((15u - 127u) << 23) + 0xFFFu)) + mantOdd, 13);

            var isNormal = Vector.GreaterThanOrEqual(bits, new Vector<uint>(113u << 23));
            var result = Vector.ConditionalSelect(isNormal, normal, subnormal);
            return result | Vector.ShiftRightLogical(sign, 16);
        }
    }
}
