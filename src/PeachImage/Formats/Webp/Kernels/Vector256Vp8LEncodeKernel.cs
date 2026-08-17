using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>SIMD tier using <see cref="Vector256{T}"/>'s cross-platform generic static API (JITs to AVX/AVX2 on x86) — 8 pixels/32 bytes at a time.</summary>
internal sealed class Vector256Vp8LEncodeKernel : IVp8LEncodeKernel
{
    private static readonly Vector256<uint> GreenMask = Vector256.Create(0x0000FF00u);

    public void SubtractGreenForward(Span<uint> pixels)
    {
        int n = Vector256<uint>.Count;
        int i = 0;

        for (; i + n <= pixels.Length; i += n)
        {
            var argb = Vector256.LoadUnsafe(ref pixels[i]);
            var green = (argb & GreenMask) >> 8;

            // Byte-lane subtract for the same reason the decode-side kernel adds in byte lanes: a uint-lane
            // subtract would let a borrow from blue propagate through green and into red.
            var greenFromRedAndBlue = ((green << 16) | green).AsByte();
            var result = (argb.AsByte() - greenFromRedAndBlue).AsUInt32();
            result.StoreUnsafe(ref pixels[i]);
        }

        for (; i < pixels.Length; i++)
        {
            pixels[i] = ScalarVp8LEncodeKernel.SubtractGreenForwardPixel(pixels[i]);
        }
    }

    public void PredictorTopForward(ReadOnlySpan<byte> row, ReadOnlySpan<byte> topRow, Span<byte> residual)
    {
        int n = Vector256<byte>.Count;
        int i = 0;

        for (; i + n <= row.Length; i += n)
        {
            var a = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(row.Slice(i, n)));
            var b = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(topRow.Slice(i, n)));
            (a - b).StoreUnsafe(ref residual[i]);
        }

        for (; i < row.Length; i++)
        {
            residual[i] = (byte)(row[i] - topRow[i]);
        }
    }
}
