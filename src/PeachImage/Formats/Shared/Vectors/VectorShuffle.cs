using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
#if !NET11_0_OR_GREATER
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace PeachImage.Formats.Shared.Vectors;

/// <summary>
/// Two-vector interleave, deinterleave, half-concatenation and reverse. On .NET 11 these forward to the portable
/// <see cref="Vector128"/>/<see cref="Vector256"/> APIs of the same name; on .NET 10, which has none of them, they
/// fall back to <c>punpckl*</c>/<c>punpckh*</c> and <c>vperm2i128</c> on x86 or <c>zip1</c>/<c>zip2</c>/<c>uzp1</c>/
/// <c>uzp2</c> on Arm64 (plus constant-mask shuffles for <see cref="Reverse{T}(Vector128{T})"/>). Results are identical
/// on both runtimes. Delete this class and call the BCL directly once .NET 10 is no longer targeted.
/// </summary>
/// <remarks>
/// Vector128 callers must gate on <see cref="IsSupported"/> and Vector256 callers on <see cref="IsSupported256"/>;
/// without it the .NET 10 fallbacks throw <see cref="PlatformNotSupportedException"/>.
/// </remarks>
internal static class VectorShuffle
{
#if NET11_0_OR_GREATER
    public static bool IsSupported => Vector128.IsHardwareAccelerated;

    public static bool IsSupported256 => Vector256.IsHardwareAccelerated;
#else
    public static bool IsSupported => Sse2.IsSupported || AdvSimd.Arm64.IsSupported;

    public static bool IsSupported256 => Avx2.IsSupported;
#endif

    /// <summary>Interleaves the low halves: <c>a0, b0, a1, b1, …</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ZipLower<T>(Vector128<T> a, Vector128<T> b)
    {
#if NET11_0_OR_GREATER
        return Vector128.ZipLower(a, b);
#else
        if (Sse2.IsSupported)
        {
            return Unsafe.SizeOf<T>() switch
            {
                1 => Sse2.UnpackLow(a.AsByte(), b.AsByte()).As<byte, T>(),
                2 => Sse2.UnpackLow(a.AsUInt16(), b.AsUInt16()).As<ushort, T>(),
                4 => Sse2.UnpackLow(a.AsUInt32(), b.AsUInt32()).As<uint, T>(),
                _ => Sse2.UnpackLow(a.AsUInt64(), b.AsUInt64()).As<ulong, T>(),
            };
        }

        return Unsafe.SizeOf<T>() switch
        {
            1 => AdvSimd.Arm64.ZipLow(a.AsByte(), b.AsByte()).As<byte, T>(),
            2 => AdvSimd.Arm64.ZipLow(a.AsUInt16(), b.AsUInt16()).As<ushort, T>(),
            4 => AdvSimd.Arm64.ZipLow(a.AsUInt32(), b.AsUInt32()).As<uint, T>(),
            _ => AdvSimd.Arm64.ZipLow(a.AsUInt64(), b.AsUInt64()).As<ulong, T>(),
        };
#endif
    }

    /// <summary>Interleaves the high halves: <c>a[n/2], b[n/2], a[n/2+1], b[n/2+1], …</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ZipUpper<T>(Vector128<T> a, Vector128<T> b)
    {
#if NET11_0_OR_GREATER
        return Vector128.ZipUpper(a, b);
#else
        if (Sse2.IsSupported)
        {
            return Unsafe.SizeOf<T>() switch
            {
                1 => Sse2.UnpackHigh(a.AsByte(), b.AsByte()).As<byte, T>(),
                2 => Sse2.UnpackHigh(a.AsUInt16(), b.AsUInt16()).As<ushort, T>(),
                4 => Sse2.UnpackHigh(a.AsUInt32(), b.AsUInt32()).As<uint, T>(),
                _ => Sse2.UnpackHigh(a.AsUInt64(), b.AsUInt64()).As<ulong, T>(),
            };
        }

        return Unsafe.SizeOf<T>() switch
        {
            1 => AdvSimd.Arm64.ZipHigh(a.AsByte(), b.AsByte()).As<byte, T>(),
            2 => AdvSimd.Arm64.ZipHigh(a.AsUInt16(), b.AsUInt16()).As<ushort, T>(),
            4 => AdvSimd.Arm64.ZipHigh(a.AsUInt32(), b.AsUInt32()).As<uint, T>(),
            _ => AdvSimd.Arm64.ZipHigh(a.AsUInt64(), b.AsUInt64()).As<ulong, T>(),
        };
#endif
    }

    /// <summary>Interleaves across the full 256-bit width (not per 128-bit lane): <c>a0, b0, a1, b1, …</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> ZipLower<T>(Vector256<T> a, Vector256<T> b)
    {
#if NET11_0_OR_GREATER
        return Vector256.ZipLower(a, b);
#else
        var (lo, hi) = UnpackInLane(a, b);
        return Avx2.Permute2x128(lo, hi, 0x20).As<byte, T>();
#endif
    }

    /// <summary>Interleaves the upper halves across the full 256-bit width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> ZipUpper<T>(Vector256<T> a, Vector256<T> b)
    {
#if NET11_0_OR_GREATER
        return Vector256.ZipUpper(a, b);
#else
        var (lo, hi) = UnpackInLane(a, b);
        return Avx2.Permute2x128(lo, hi, 0x31).As<byte, T>();
#endif
    }

    /// <summary>Even-indexed elements of <paramref name="a"/> then of <paramref name="b"/>: <c>a0, a2, …, b0, b2, …</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> UnzipEven<T>(Vector128<T> a, Vector128<T> b)
    {
#if NET11_0_OR_GREATER
        return Vector128.UnzipEven(a, b);
#else
        if (AdvSimd.Arm64.IsSupported)
        {
            return Unsafe.SizeOf<T>() switch
            {
                1 => AdvSimd.Arm64.UnzipEven(a.AsByte(), b.AsByte()).As<byte, T>(),
                2 => AdvSimd.Arm64.UnzipEven(a.AsUInt16(), b.AsUInt16()).As<ushort, T>(),
                4 => AdvSimd.Arm64.UnzipEven(a.AsUInt32(), b.AsUInt32()).As<uint, T>(),
                _ => AdvSimd.Arm64.UnzipEven(a.AsUInt64(), b.AsUInt64()).As<ulong, T>(),
            };
        }

        return Vector128.Create(GatherHalf(a, 0), GatherHalf(b, 0)).As<ulong, T>();
#endif
    }

    /// <summary>Odd-indexed elements of <paramref name="a"/> then of <paramref name="b"/>: <c>a1, a3, …, b1, b3, …</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> UnzipOdd<T>(Vector128<T> a, Vector128<T> b)
    {
#if NET11_0_OR_GREATER
        return Vector128.UnzipOdd(a, b);
#else
        if (AdvSimd.Arm64.IsSupported)
        {
            return Unsafe.SizeOf<T>() switch
            {
                1 => AdvSimd.Arm64.UnzipOdd(a.AsByte(), b.AsByte()).As<byte, T>(),
                2 => AdvSimd.Arm64.UnzipOdd(a.AsUInt16(), b.AsUInt16()).As<ushort, T>(),
                4 => AdvSimd.Arm64.UnzipOdd(a.AsUInt32(), b.AsUInt32()).As<uint, T>(),
                _ => AdvSimd.Arm64.UnzipOdd(a.AsUInt64(), b.AsUInt64()).As<ulong, T>(),
            };
        }

        return Vector128.Create(GatherHalf(a, 1), GatherHalf(b, 1)).As<ulong, T>();
#endif
    }

    /// <summary>Lower half of <paramref name="a"/>, then lower half of <paramref name="b"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ConcatLowerLower<T>(Vector128<T> a, Vector128<T> b) =>
#if NET11_0_OR_GREATER
        Vector128.ConcatLowerLower(a, b);
#else
        Vector128.Create(a.GetLower(), b.GetLower());
#endif

    /// <summary>Upper half of <paramref name="a"/>, then upper half of <paramref name="b"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ConcatUpperUpper<T>(Vector128<T> a, Vector128<T> b) =>
#if NET11_0_OR_GREATER
        Vector128.ConcatUpperUpper(a, b);
#else
        Vector128.Create(a.GetUpper(), b.GetUpper());
#endif

    /// <summary>Lower 128 bits of <paramref name="a"/>, then lower 128 bits of <paramref name="b"/> (<c>vperm2i128 0x20</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> ConcatLowerLower<T>(Vector256<T> a, Vector256<T> b) =>
#if NET11_0_OR_GREATER
        Vector256.ConcatLowerLower(a, b);
#else
        Avx2.Permute2x128(a.AsByte(), b.AsByte(), 0x20).As<byte, T>();
#endif

    /// <summary>Lower 128 bits of <paramref name="a"/>, then upper 128 bits of <paramref name="b"/> (<c>vperm2i128 0x30</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> ConcatLowerUpper<T>(Vector256<T> a, Vector256<T> b) =>
#if NET11_0_OR_GREATER
        Vector256.ConcatLowerUpper(a, b);
#else
        Avx2.Permute2x128(a.AsByte(), b.AsByte(), 0x30).As<byte, T>();
#endif

    /// <summary>Upper 128 bits of <paramref name="a"/>, then upper 128 bits of <paramref name="b"/> (<c>vperm2i128 0x31</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> ConcatUpperUpper<T>(Vector256<T> a, Vector256<T> b) =>
#if NET11_0_OR_GREATER
        Vector256.ConcatUpperUpper(a, b);
#else
        Avx2.Permute2x128(a.AsByte(), b.AsByte(), 0x31).As<byte, T>();
#endif

    /// <summary>Reverses element order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> Reverse<T>(Vector128<T> v)
    {
#if NET11_0_OR_GREATER
        return Vector128.Reverse(v);
#else
        return Unsafe.SizeOf<T>() switch
        {
            1 => Vector128.Shuffle(v.AsByte(), Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0)).As<byte, T>(),
            2 => Vector128.Shuffle(v.AsUInt16(), Vector128.Create((ushort)7, 6, 5, 4, 3, 2, 1, 0)).As<ushort, T>(),
            4 => Vector128.Shuffle(v.AsUInt32(), Vector128.Create(3u, 2, 1, 0)).As<uint, T>(),
            _ => Vector128.Shuffle(v.AsUInt64(), Vector128.Create(1ul, 0)).As<ulong, T>(),
        };
#endif
    }

    /// <summary>Reverses element order across the full 256-bit width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> Reverse<T>(Vector256<T> v)
    {
#if NET11_0_OR_GREATER
        return Vector256.Reverse(v);
#else
        var lanes = Vector256.Create(Reverse(v.GetUpper()), Reverse(v.GetLower()));
        return lanes;
#endif
    }

#if !NET11_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector256<byte> Lo, Vector256<byte> Hi) UnpackInLane<T>(Vector256<T> a, Vector256<T> b) =>
        Unsafe.SizeOf<T>() switch
        {
            1 => (Avx2.UnpackLow(a.AsByte(), b.AsByte()), Avx2.UnpackHigh(a.AsByte(), b.AsByte())),
            2 => (Avx2.UnpackLow(a.AsUInt16(), b.AsUInt16()).AsByte(), Avx2.UnpackHigh(a.AsUInt16(), b.AsUInt16()).AsByte()),
            4 => (Avx2.UnpackLow(a.AsUInt32(), b.AsUInt32()).AsByte(), Avx2.UnpackHigh(a.AsUInt32(), b.AsUInt32()).AsByte()),
            _ => (Avx2.UnpackLow(a.AsUInt64(), b.AsUInt64()).AsByte(), Avx2.UnpackHigh(a.AsUInt64(), b.AsUInt64()).AsByte()),
        };

    /// <summary>The even (<paramref name="phase"/> 0) or odd (1) elements of <paramref name="v"/>, packed into 64 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GatherHalf<T>(Vector128<T> v, int phase)
    {
        var bytes = v.AsByte();
        var picked = Unsafe.SizeOf<T>() switch
        {
            1 => phase == 0
                ? Vector128.Shuffle(bytes, Vector128.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 0, 0, 0, 0, 0, 0, 0, 0))
                : Vector128.Shuffle(bytes, Vector128.Create((byte)1, 3, 5, 7, 9, 11, 13, 15, 0, 0, 0, 0, 0, 0, 0, 0)),
            2 => phase == 0
                ? Vector128.Shuffle(bytes, Vector128.Create((byte)0, 1, 4, 5, 8, 9, 12, 13, 0, 0, 0, 0, 0, 0, 0, 0))
                : Vector128.Shuffle(bytes, Vector128.Create((byte)2, 3, 6, 7, 10, 11, 14, 15, 0, 0, 0, 0, 0, 0, 0, 0)),
            4 => phase == 0
                ? Vector128.Shuffle(bytes, Vector128.Create((byte)0, 1, 2, 3, 8, 9, 10, 11, 0, 0, 0, 0, 0, 0, 0, 0))
                : Vector128.Shuffle(bytes, Vector128.Create((byte)4, 5, 6, 7, 12, 13, 14, 15, 0, 0, 0, 0, 0, 0, 0, 0)),
            _ => phase == 0 ? bytes : Vector128.Shuffle(bytes, Vector128.Create((byte)8, 9, 10, 11, 12, 13, 14, 15, 0, 0, 0, 0, 0, 0, 0, 0)),
        };
        return picked.AsUInt64().ToScalar();
    }
#endif
}
