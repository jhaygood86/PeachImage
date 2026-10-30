using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// Moves samples between a plane's storage type (<see cref="byte"/> for 8-bit, <see cref="ushort"/> for
/// higher depths, <see cref="int"/> for the encoder's own planes) and the <see cref="int"/> lanes the
/// kernels compute in. Every branch on the sample type folds away when the JIT specialises a generic caller
/// for a value type, so each instantiation contains only its own load/store sequence.
/// </summary>
internal static class Av1PixelVector
{
    /// <summary>Loads eight consecutive samples as <see cref="int"/> lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<int> Load8<TPixel>(ref TPixel source)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (typeof(TPixel) == typeof(int))
        {
            return Vector256.LoadUnsafe(ref Unsafe.As<TPixel, int>(ref source));
        }

        if (typeof(TPixel) == typeof(ushort))
        {
            return Widen(Vector128.LoadUnsafe(ref Unsafe.As<TPixel, ushort>(ref source)));
        }

        // byte: eight samples are one 64-bit load.
        var bytes = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<TPixel, byte>(ref source))).AsByte();
        return Widen(Vector128.WidenLower(bytes));
    }

    /// <summary>Stores eight lanes (already clamped to the sample range) as consecutive samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store8<TPixel>(Vector256<int> value, ref TPixel destination)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (typeof(TPixel) == typeof(int))
        {
            value.StoreUnsafe(ref Unsafe.As<TPixel, int>(ref destination));
            return;
        }

        var narrowed = Vector256.Narrow(value.AsUInt32(), value.AsUInt32()).GetLower();
        if (typeof(TPixel) == typeof(ushort))
        {
            narrowed.StoreUnsafe(ref Unsafe.As<TPixel, ushort>(ref destination));
            return;
        }

        var bytes = Vector128.Narrow(narrowed, narrowed);
        Unsafe.WriteUnaligned(ref Unsafe.As<TPixel, byte>(ref destination), bytes.AsUInt64().ToScalar());
    }

    /// <summary>Loads four consecutive samples as <see cref="int"/> lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Load4<TPixel>(ref TPixel source)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (typeof(TPixel) == typeof(int))
        {
            return Vector128.LoadUnsafe(ref Unsafe.As<TPixel, int>(ref source));
        }

        if (typeof(TPixel) == typeof(ushort))
        {
            var words = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<TPixel, byte>(ref source))).AsUInt16();
            return Vector128.WidenLower(words).AsInt32();
        }

        var bytes = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.As<TPixel, byte>(ref source))).AsByte();
        return Vector128.WidenLower(Vector128.WidenLower(bytes)).AsInt32();
    }

    /// <summary>Stores four lanes (already clamped to the sample range) as consecutive samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store4<TPixel>(Vector128<int> value, ref TPixel destination)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (typeof(TPixel) == typeof(int))
        {
            value.StoreUnsafe(ref Unsafe.As<TPixel, int>(ref destination));
            return;
        }

        var words = Vector128.Narrow(value.AsUInt32(), value.AsUInt32());
        if (typeof(TPixel) == typeof(ushort))
        {
            Unsafe.WriteUnaligned(ref Unsafe.As<TPixel, byte>(ref destination), words.AsUInt64().ToScalar());
            return;
        }

        var bytes = Vector128.Narrow(words, words);
        Unsafe.WriteUnaligned(ref Unsafe.As<TPixel, byte>(ref destination), bytes.AsUInt32().ToScalar());
    }

    /// <summary>Narrows <paramref name="source"/> into <paramref name="destination"/> (same length); values must already be in range.</summary>
    public static void StoreRow<TPixel>(ReadOnlySpan<int> source, Span<TPixel> destination)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= source.Length; i += 8)
            {
                Store8(Vector256.LoadUnsafe(in source[i]), ref destination[i]);
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] = TPixel.CreateTruncating(source[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Widen(Vector128<ushort> v) =>
        Vector256.Create(Vector128.WidenLower(v), Vector128.WidenUpper(v)).AsInt32();
}
