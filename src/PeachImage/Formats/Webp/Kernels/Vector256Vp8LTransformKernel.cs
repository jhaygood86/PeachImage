using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>SIMD tier using <see cref="Vector256{T}"/>'s cross-platform generic static API (JITs to AVX/AVX2 on x86) — 8 pixels/32 bytes at a time.</summary>
internal sealed class Vector256Vp8LTransformKernel : IVp8LTransformKernel
{
    private static readonly Vector256<uint> GreenMask = Vector256.Create(0x0000FF00u);
    private static readonly Vector256<int> ByteMask = Vector256.Create(0xFF);
    private static readonly Vector256<int> KeepAlphaGreenMask = Vector256.Create(unchecked((int)0xFF00FF00));

    public void SubtractGreenInverse(Span<uint> pixels)
    {
        int n = Vector256<uint>.Count;
        int i = 0;

        for (; i + n <= pixels.Length; i += n)
        {
            var argb = Vector256.LoadUnsafe(ref pixels[i]);
            var green = (argb & GreenMask) >> 8;

            // Adding in *byte* lanes rather than uint lanes is what makes the per-channel "mod 256, no clamp"
            // wraparound correct: a uint-lane add lets the carry out of blue run through green and into red,
            // which silently corrupted red for every pixel with green == 255 and blue >= 1.
            // Laid out little-endian, a uint 0xAARRGGBB has bytes [BB, GG, RR, AA], so broadcasting green into
            // 0x00GG00GG gives byte lanes [GG, 0, GG, 0] -- exactly green added to blue and red, nothing to
            // green and alpha.
            var greenIntoRedAndBlue = ((green << 16) | green).AsByte();
            var result = (argb.AsByte() + greenIntoRedAndBlue).AsUInt32();
            result.StoreUnsafe(ref pixels[i]);
        }

        for (; i < pixels.Length; i++)
        {
            pixels[i] = ScalarVp8LTransformKernel.SubtractGreenInversePixel(pixels[i]);
        }
    }

    public void PredictorTopInverse(Span<byte> row, ReadOnlySpan<byte> topRow)
    {
        int n = Vector256<byte>.Count;
        int i = 0;

        for (; i + n <= row.Length; i += n)
        {
            var a = Vector256.LoadUnsafe(ref row[i]);
            var b = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(topRow.Slice(i, n)));
            (a + b).StoreUnsafe(ref row[i]);
        }

        for (; i < row.Length; i++)
        {
            row[i] = (byte)(row[i] + topRow[i]);
        }
    }

    public void ColorTransformInverse(Span<uint> pixels, sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue)
    {
        int n = Vector256<int>.Count;
        int i = 0;
        var gtr = Vector256.Create((int)greenToRed);
        var gtb = Vector256.Create((int)greenToBlue);
        var rtb = Vector256.Create((int)redToBlue);

        for (; i + n <= pixels.Length; i += n)
        {
            var argb = Vector256.LoadUnsafe(ref pixels[i]).AsInt32();

            // Sign-extend the green byte (bits 8-15) to a full int32 lane, matching scalar `(sbyte)(argb >> 8)`
            // — see Vector128Vp8LTransformKernel.ColorTransformInverse for the same approach at half width.
            var green = SignExtendByte(Vector256.ShiftRightLogical(argb, 8) & ByteMask);

            var red = Vector256.ShiftRightLogical(argb, 16) & ByteMask;
            var blue = argb & ByteMask;

            red = (red + Vector256.ShiftRightArithmetic(gtr * green, 5)) & ByteMask;
            var redSigned = SignExtendByte(red);

            blue = (blue + Vector256.ShiftRightArithmetic(gtb * green, 5)) & ByteMask;
            blue = (blue + Vector256.ShiftRightArithmetic(rtb * redSigned, 5)) & ByteMask;

            var result = (argb & KeepAlphaGreenMask) | (red << 16) | blue;
            result.AsUInt32().StoreUnsafe(ref pixels[i]);
        }

        for (; i < pixels.Length; i++)
        {
            pixels[i] = ScalarVp8LTransformKernel.ColorTransformInversePixel(pixels[i], greenToRed, greenToBlue, redToBlue);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> SignExtendByte(Vector256<int> unsignedByteInLowLane) =>
        Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(unsignedByteInLowLane, 24), 24);
}
