using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>SIMD tier using <see cref="Vector128{T}"/>'s cross-platform generic static API (JITs to SSE2 on x86, AdvSimd on Arm) — 4 pixels/16 bytes at a time.</summary>
internal sealed class Vector128Vp8LTransformKernel : IVp8LTransformKernel
{
    private static readonly Vector128<uint> GreenMask = Vector128.Create(0x0000FF00u);

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
}
