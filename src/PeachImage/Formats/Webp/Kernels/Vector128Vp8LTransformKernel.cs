using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>SIMD tier using <see cref="Vector128{T}"/>'s cross-platform generic static API (JITs to SSE2 on x86, AdvSimd on Arm) — 4 pixels/16 bytes at a time.</summary>
internal sealed class Vector128Vp8LTransformKernel : IVp8LTransformKernel
{
    private static readonly Vector128<uint> GreenMask = Vector128.Create(0x0000FF00u);
    private static readonly Vector128<int> ByteMask = Vector128.Create(0xFF);
    private static readonly Vector128<int> KeepAlphaGreenMask = Vector128.Create(unchecked((int)0xFF00FF00));

    public void SubtractGreenInverse(Span<uint> pixels)
    {
        int n = Vector128<uint>.Count;
        int i = 0;

        for (; i + n <= pixels.Length; i += n)
        {
            var argb = Vector128.LoadUnsafe(ref pixels[i]);
            var green = (argb & GreenMask) >> 8;

            // Byte-lane add, for the same reason as Vector256Vp8LTransformKernel.SubtractGreenInverse -- see
            // the comment there: a uint-lane add carries out of blue, through green, into red.
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
        int n = Vector128<byte>.Count;
        int i = 0;

        for (; i + n <= row.Length; i += n)
        {
            var a = Vector128.LoadUnsafe(ref row[i]);
            var b = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(topRow.Slice(i, n)));
            (a + b).StoreUnsafe(ref row[i]);
        }

        for (; i < row.Length; i++)
        {
            row[i] = (byte)(row[i] + topRow[i]);
        }
    }

    public void ColorTransformInverse(Span<uint> pixels, sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue)
    {
        int n = Vector128<int>.Count;
        int i = 0;
        var gtr = Vector128.Create((int)greenToRed);
        var gtb = Vector128.Create((int)greenToBlue);
        var rtb = Vector128.Create((int)redToBlue);

        for (; i + n <= pixels.Length; i += n)
        {
            var argb = Vector128.LoadUnsafe(ref pixels[i]).AsInt32();

            // Sign-extend the green byte (bits 8-15) to a full int32 lane, matching scalar `(sbyte)(argb >> 8)`:
            // isolate the byte with an unsigned shift + mask, then shift-left/arithmetic-shift-right by 24 to
            // reproduce an 8-to-32-bit sign extension.
            var green = SignExtendByte(Vector128.ShiftRightLogical(argb, 8) & ByteMask);

            var red = Vector128.ShiftRightLogical(argb, 16) & ByteMask;
            var blue = argb & ByteMask;

            red = (red + Vector128.ShiftRightArithmetic(gtr * green, 5)) & ByteMask;
            var redSigned = SignExtendByte(red);

            blue = (blue + Vector128.ShiftRightArithmetic(gtb * green, 5)) & ByteMask;
            blue = (blue + Vector128.ShiftRightArithmetic(rtb * redSigned, 5)) & ByteMask;

            var result = (argb & KeepAlphaGreenMask) | (red << 16) | blue;
            result.AsUInt32().StoreUnsafe(ref pixels[i]);
        }

        for (; i < pixels.Length; i++)
        {
            pixels[i] = ScalarVp8LTransformKernel.ColorTransformInversePixel(pixels[i], greenToRed, greenToBlue, redToBlue);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> SignExtendByte(Vector128<int> unsignedByteInLowLane) =>
        Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(unsignedByteInLowLane, 24), 24);
}
