using System.Numerics;
using System.Runtime.CompilerServices;

namespace PeachImage.Formats.Shared.Arithmetic;

/// <summary>Integer helpers shared across codecs.</summary>
internal static class IntMath
{
    /// <summary>
    /// <c>⌈a / b⌉</c> for any signs, without the overflow of <c>(a + b - 1) / b</c>. Uses
    /// <c>IBinaryInteger.Divide(…, DivisionRounding.Ceiling)</c> on .NET 11; the .NET 10 fallback computes the same value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CeilDiv(int a, int b) => CeilDiv<int>(a, b);

    /// <inheritdoc cref="CeilDiv(int, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long CeilDiv(long a, long b) => CeilDiv<long>(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T CeilDiv<T>(T a, T b)
        where T : IBinaryInteger<T>
    {
#if NET11_0_OR_GREATER
        return T.Divide(a, b, DivisionRounding.Ceiling);
#else
        var (quotient, remainder) = T.DivRem(a, b);
        return !T.IsZero(remainder) && (T.IsNegative(a) == T.IsNegative(b)) ? quotient + T.One : quotient;
#endif
    }
}
