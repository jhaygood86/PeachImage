using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;

/// <summary>
/// 16-pixels-per-iteration YUV-&gt;RGB tier built on 16-bit lanes and <c>pmulhuw</c>, the shape libwebp's SSE2
/// converter (<c>src/dsp/yuv_sse2.c</c>) uses: a sample promoted to <c>value &lt;&lt; 8</c> and multiplied
/// by a 16-bit coefficient keeping the high half is exactly the scalar tier's <c>(value * coeff) &gt;&gt; 8</c>, so
/// each product is truncated individually as the scalar tier requires. The three channel sums are formed with
/// unsigned saturating adds and subtracts, which reproduce the scalar tier's "negative result clamps to zero"
/// because every subtraction follows the additions (so saturating at zero early cannot change a result that
/// would end below zero anyway) and no sum can reach 65535. The final <c>&gt;&gt; 6</c> and 0..255 clamp is a
/// logical shift plus <c>packuswb</c>. Output is identical to <see cref="Vp8ScalarColorConverter"/> for every
/// possible sample triple (pinned exhaustively by the tests).
/// </summary>
internal sealed class Sse2Vp8ColorConverter : IVp8ColorConverter
{
    /// <summary>Pixels converted per vector iteration.</summary>
    private const int Lanes = 16;

    /// <summary>The interleave below uses <c>pshufb</c>; without SSSE3 the portable shuffle would be far slower than the tier it replaces.</summary>
    public static bool IsSupported => Sse2.IsSupported && Ssse3.IsSupported;

    // Index tables for expanding planar R, G, B (16 bytes each) into three 16-byte runs of packed RGB.
    // Output byte k of run j holds channel (k + 16j) % 3 of pixel (k + 16j) / 3; 0xFF zeroes a lane.
    private static readonly Vector128<byte>[] RgbShuffle = BuildRgbShuffles();

    /// <inheritdoc/>
    public void ConvertRow(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, Span<byte> rgb, int width)
    {
        int x = 0;

        if (width >= Lanes)
        {
            // Bounds for every vector access below follow from x + Lanes <= width and the span-length checks.
            if (y.Length < width || u.Length < width || v.Length < width || rgb.Length < width * 3)
            {
                throw new ArgumentException("Row buffers are shorter than the row width.");
            }

            ref byte yRef = ref MemoryMarshal.GetReference(y);
            ref byte uRef = ref MemoryMarshal.GetReference(u);
            ref byte vRef = ref MemoryMarshal.GetReference(v);
            ref byte outRef = ref MemoryMarshal.GetReference(rgb);

            Vector128<byte> shuffle0Red = RgbShuffle[0], shuffle0Green = RgbShuffle[1], shuffle0Blue = RgbShuffle[2];
            Vector128<byte> shuffle1Red = RgbShuffle[3], shuffle1Green = RgbShuffle[4], shuffle1Blue = RgbShuffle[5];
            Vector128<byte> shuffle2Red = RgbShuffle[6], shuffle2Green = RgbShuffle[7], shuffle2Blue = RgbShuffle[8];

            for (; x + Lanes <= width; x += Lanes)
            {
                Convert16(
                    Vector128.LoadUnsafe(ref yRef, (nuint)x),
                    Vector128.LoadUnsafe(ref uRef, (nuint)x),
                    Vector128.LoadUnsafe(ref vRef, (nuint)x),
                    out var red,
                    out var green,
                    out var blue);

                ref byte dst = ref Unsafe.Add(ref outRef, x * 3);
                (Vector128.Shuffle(red, shuffle0Red) | Vector128.Shuffle(green, shuffle0Green) | Vector128.Shuffle(blue, shuffle0Blue)).StoreUnsafe(ref dst);
                (Vector128.Shuffle(red, shuffle1Red) | Vector128.Shuffle(green, shuffle1Green) | Vector128.Shuffle(blue, shuffle1Blue)).StoreUnsafe(ref dst, 16);
                (Vector128.Shuffle(red, shuffle2Red) | Vector128.Shuffle(green, shuffle2Green) | Vector128.Shuffle(blue, shuffle2Blue)).StoreUnsafe(ref dst, 32);
            }
        }

        Vp8ScalarColorConverter.ConvertRemainder(y, u, v, rgb, x, width);
    }

    /// <summary>
    /// As <see cref="ConvertRow"/> but writes RGBA, taking each pixel's alpha from <paramref name="alpha"/>, so
    /// an image with an alpha plane needs neither a scratch RGB row nor a per-pixel splice pass afterward.
    /// </summary>
    public void ConvertRowWithAlpha(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, ReadOnlySpan<byte> alpha, Span<byte> rgba, int width)
    {
        if (y.Length < width || u.Length < width || v.Length < width || alpha.Length < width || rgba.Length < width * 4)
        {
            throw new ArgumentException("Row buffers are shorter than the row width.");
        }

        ref byte yRef = ref MemoryMarshal.GetReference(y);
        ref byte uRef = ref MemoryMarshal.GetReference(u);
        ref byte vRef = ref MemoryMarshal.GetReference(v);
        ref byte aRef = ref MemoryMarshal.GetReference(alpha);
        ref byte outRef = ref MemoryMarshal.GetReference(rgba);

        int x = 0;
        for (; x + Lanes <= width; x += Lanes)
        {
            Convert16(
                Vector128.LoadUnsafe(ref yRef, (nuint)x),
                Vector128.LoadUnsafe(ref uRef, (nuint)x),
                Vector128.LoadUnsafe(ref vRef, (nuint)x),
                out var red,
                out var green,
                out var blue);

            var a = Vector128.LoadUnsafe(ref aRef, (nuint)x);
            var redGreenLow = Sse2.UnpackLow(red, green).AsUInt16();
            var redGreenHigh = Sse2.UnpackHigh(red, green).AsUInt16();
            var blueAlphaLow = Sse2.UnpackLow(blue, a).AsUInt16();
            var blueAlphaHigh = Sse2.UnpackHigh(blue, a).AsUInt16();

            ref byte dst = ref Unsafe.Add(ref outRef, x * 4);
            Sse2.UnpackLow(redGreenLow, blueAlphaLow).AsByte().StoreUnsafe(ref dst);
            Sse2.UnpackHigh(redGreenLow, blueAlphaLow).AsByte().StoreUnsafe(ref dst, 16);
            Sse2.UnpackLow(redGreenHigh, blueAlphaHigh).AsByte().StoreUnsafe(ref dst, 32);
            Sse2.UnpackHigh(redGreenHigh, blueAlphaHigh).AsByte().StoreUnsafe(ref dst, 48);
        }

        Span<byte> pixel = stackalloc byte[3];
        for (; x < width; x++)
        {
            Vp8ScalarColorConverter.ConvertPixel(y[x], u[x], v[x], pixel);
            rgba[(x * 4) + 0] = pixel[0];
            rgba[(x * 4) + 1] = pixel[1];
            rgba[(x * 4) + 2] = pixel[2];
            rgba[(x * 4) + 3] = alpha[x];
        }
    }

    /// <summary>Converts 16 pixels to planar R, G and B bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Convert16(
        Vector128<byte> y,
        Vector128<byte> u,
        Vector128<byte> v,
        out Vector128<byte> red,
        out Vector128<byte> green,
        out Vector128<byte> blue)
    {
        // Interleaving with zero in the low byte promotes each sample s to the 16-bit value s << 8.
        var zero = Vector128<byte>.Zero;
        Convert8(Sse2.UnpackLow(zero, y).AsUInt16(), Sse2.UnpackLow(zero, u).AsUInt16(), Sse2.UnpackLow(zero, v).AsUInt16(), out var redLow, out var greenLow, out var blueLow);
        Convert8(Sse2.UnpackHigh(zero, y).AsUInt16(), Sse2.UnpackHigh(zero, u).AsUInt16(), Sse2.UnpackHigh(zero, v).AsUInt16(), out var redHigh, out var greenHigh, out var blueHigh);

        red = Sse2.PackUnsignedSaturate(redLow, redHigh);
        green = Sse2.PackUnsignedSaturate(greenLow, greenHigh);
        blue = Sse2.PackUnsignedSaturate(blueLow, blueHigh);
    }

    /// <summary>Converts 8 pixels whose samples are already scaled to <c>s &lt;&lt; 8</c>; results are the scalar tier's values after <c>&gt;&gt; 6</c>, before the 0..255 clamp.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Convert8(
        Vector128<ushort> y16,
        Vector128<ushort> u16,
        Vector128<ushort> v16,
        out Vector128<short> red,
        out Vector128<short> green,
        out Vector128<short> blue)
    {
        var luma = Sse2.MultiplyHigh(y16, Vector128.Create((ushort)Vp8ScalarColorConverter.YCoefficient));
        var vToRed = Sse2.MultiplyHigh(v16, Vector128.Create((ushort)Vp8ScalarColorConverter.VToRed));
        var uToGreen = Sse2.MultiplyHigh(u16, Vector128.Create((ushort)Vp8ScalarColorConverter.UToGreen));
        var vToGreen = Sse2.MultiplyHigh(v16, Vector128.Create((ushort)Vp8ScalarColorConverter.VToGreen));
        var uToBlue = Sse2.MultiplyHigh(u16, Vector128.Create((ushort)Vp8ScalarColorConverter.UToBlue));

        var r = Sse2.SubtractSaturate(Sse2.AddSaturate(luma, vToRed), Vector128.Create((ushort)-Vp8ScalarColorConverter.RedBias));
        var g = Sse2.SubtractSaturate(
            Sse2.SubtractSaturate(Sse2.AddSaturate(luma, Vector128.Create((ushort)Vp8ScalarColorConverter.GreenBias)), uToGreen),
            vToGreen);
        var b = Sse2.SubtractSaturate(Sse2.AddSaturate(luma, uToBlue), Vector128.Create((ushort)-Vp8ScalarColorConverter.BlueBias));

        // At most 0xFFFF >> 6 = 1023, so the signed 16-bit reinterpretation is lossless and packuswb clamps to 255.
        red = Sse2.ShiftRightLogical(r, 6).AsInt16();
        green = Sse2.ShiftRightLogical(g, 6).AsInt16();
        blue = Sse2.ShiftRightLogical(b, 6).AsInt16();
    }

    private static Vector128<byte>[] BuildRgbShuffles()
    {
        // [run * 3 + channel]: channel 0 = red, 1 = green, 2 = blue.
        var tables = new Vector128<byte>[9];
        Span<byte> indices = stackalloc byte[16];
        for (int run = 0; run < 3; run++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                for (int k = 0; k < 16; k++)
                {
                    int global = (run * 16) + k;
                    indices[k] = global % 3 == channel ? (byte)(global / 3) : (byte)0xFF;
                }

                tables[(run * 3) + channel] = Vector128.Create<byte>(indices);
            }
        }

        return tables;
    }
}
