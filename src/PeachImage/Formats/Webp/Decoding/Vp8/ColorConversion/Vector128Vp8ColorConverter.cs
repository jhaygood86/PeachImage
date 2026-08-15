using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;

/// <summary>
/// Vectorized <see cref="IVp8ColorConverter"/> tier: four pixels per iteration through
/// <see cref="Vector128{T}"/>'s cross-platform generic static API (SSE2 on x86, AdvSimd on Arm).
/// </summary>
/// <remarks>
/// <para>
/// Works in 32-bit lanes rather than the 16-bit lanes libwebp's SSE2 path uses. libwebp reaches for
/// <c>_mm_mulhi_epu16</c> there, which .NET's portable vector API has no equivalent of; widening to
/// <see cref="int"/> instead lets each lane evaluate the <em>literal</em> scalar expression from
/// <see cref="Vp8ScalarColorConverter.ConvertPixel"/> — including truncating each product to 8 fractional bits
/// individually, before summing, which is not the same as descaling the sum — so agreement with the scalar
/// tier is by construction rather than by argument. There is ample headroom: the largest intermediate is
/// 255 * 33050 = 8,427,750.
/// </para>
/// <para>
/// The interleaved store is the same trick as Jpeg's <c>Vector256ColorConverter.StoreInterleavedRgb</c> — three
/// byte shuffles OR'd together — rather than a per-lane stride-3 scalar store loop.
/// </para>
/// </remarks>
internal sealed class Vector128Vp8ColorConverter : IVp8ColorConverter
{
    /// <summary>Pixels converted per vector iteration.</summary>
    public const int Lanes = 4;

    private const byte Skip = 0xFF; // Any index at or past Vector128<byte>.Count makes Vector128.Shuffle emit zero.

    private static readonly Vector128<byte> RedLanes = Vector128.Create(
        (byte)0, Skip, Skip, 1, Skip, Skip, 2, Skip, Skip, 3, Skip, Skip, Skip, Skip, Skip, Skip);

    private static readonly Vector128<byte> GreenLanes = Vector128.Create(
        Skip, (byte)0, Skip, Skip, 1, Skip, Skip, 2, Skip, Skip, 3, Skip, Skip, Skip, Skip, Skip);

    private static readonly Vector128<byte> BlueLanes = Vector128.Create(
        Skip, Skip, (byte)0, Skip, Skip, 1, Skip, Skip, 2, Skip, Skip, 3, Skip, Skip, Skip, Skip);

    /// <inheritdoc/>
    public void ConvertRow(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, Span<byte> rgb, int width)
    {
        int x = 0;

        // The interleaved store writes a full 16 bytes but only 12 belong to this iteration; the other 4 are
        // rewritten by the next one. So the vector loop has to stop while 16 bytes of destination still remain,
        // and the last pixels of the row fall to the scalar tail.
        for (; x + Lanes <= width && (x * 3) + Vector128<byte>.Count <= rgb.Length; x += Lanes)
        {
            var luma = MultiplyHighByte(Widen(y, x), Vp8ScalarColorConverter.YCoefficient);
            var chromaU = Widen(u, x);
            var chromaV = Widen(v, x);

            var red = luma
                + MultiplyHighByte(chromaV, Vp8ScalarColorConverter.VToRed)
                + Vector128.Create(Vp8ScalarColorConverter.RedBias);

            var green = luma
                - MultiplyHighByte(chromaU, Vp8ScalarColorConverter.UToGreen)
                - MultiplyHighByte(chromaV, Vp8ScalarColorConverter.VToGreen)
                + Vector128.Create(Vp8ScalarColorConverter.GreenBias);

            var blue = luma
                + MultiplyHighByte(chromaU, Vp8ScalarColorConverter.UToBlue)
                + Vector128.Create(Vp8ScalarColorConverter.BlueBias);

            var interleaved =
                Vector128.Shuffle(DescaleAndPack(red), RedLanes) |
                Vector128.Shuffle(DescaleAndPack(green), GreenLanes) |
                Vector128.Shuffle(DescaleAndPack(blue), BlueLanes);

            interleaved.CopyTo(rgb[(x * 3)..]);
        }

        Vp8ScalarColorConverter.ConvertRemainder(y, u, v, rgb, x, width);
    }

    /// <summary>Loads <see cref="Lanes"/> consecutive samples into 32-bit lanes.</summary>
    private static Vector128<int> Widen(ReadOnlySpan<byte> source, int offset) =>
        Vector128.Create((int)source[offset], source[offset + 1], source[offset + 2], source[offset + 3]);

    /// <summary>The scalar tier's <c>MultHi</c>: multiply, then keep 8 fractional bits.</summary>
    private static Vector128<int> MultiplyHighByte(Vector128<int> value, int coefficient) =>
        Vector128.ShiftRightArithmetic(value * Vector128.Create(coefficient), 8);

    /// <summary>Applies the final <c>&gt;&gt; 6</c> descale, clamps to [0,255], and packs the four results into the low four bytes.</summary>
    private static Vector128<byte> DescaleAndPack(Vector128<int> value)
    {
        var descaled = Vector128.ShiftRightArithmetic(value, 6);
        var clamped = Vector128.Min(Vector128.Max(descaled, Vector128<int>.Zero), Vector128.Create(255));

        // Both narrowings truncate rather than saturate, which is exactly what's wanted after the clamp above:
        // every lane already holds 0..255, so the low bytes survive intact.
        return Vector128.Narrow(Vector128.Narrow(clamped, Vector128<int>.Zero), Vector128<short>.Zero).AsByte();
    }
}
