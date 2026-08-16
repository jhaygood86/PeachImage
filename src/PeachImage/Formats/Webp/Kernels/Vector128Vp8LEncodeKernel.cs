using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>SIMD tier using <see cref="Vector128{T}"/>'s cross-platform generic static API (JITs to SSE2 on x86, AdvSimd on Arm) — 4 pixels/16 bytes at a time.</summary>
internal sealed class Vector128Vp8LEncodeKernel : IVp8LEncodeKernel
{
    private static readonly Vector128<uint> GreenMask = Vector128.Create(0x0000FF00u);

    public void SubtractGreenForward(Span<uint> pixels)
    {
        int n = Vector128<uint>.Count;
        int i = 0;

        for (; i + n <= pixels.Length; i += n)
        {
            var argb = Vector128.Create(pixels.Slice(i, n));
            var green = (argb & GreenMask) >> 8;

            // Byte-lane subtract for the same reason the decode-side kernel adds in byte lanes: a uint-lane
            // subtract would let a borrow from blue propagate through green and into red.
            var greenFromRedAndBlue = ((green << 16) | green).AsByte();
            var result = (argb.AsByte() - greenFromRedAndBlue).AsUInt32();
            result.CopyTo(pixels.Slice(i, n));
        }

        for (; i < pixels.Length; i++)
        {
            pixels[i] = ScalarVp8LEncodeKernel.SubtractGreenForwardPixel(pixels[i]);
        }
    }

    public void PredictorTopForward(ReadOnlySpan<byte> row, ReadOnlySpan<byte> topRow, Span<byte> residual)
    {
        int n = Vector128<byte>.Count;
        int i = 0;

        for (; i + n <= row.Length; i += n)
        {
            var a = Vector128.Create(row.Slice(i, n));
            var b = Vector128.Create(topRow.Slice(i, n));
            (a - b).CopyTo(residual.Slice(i, n));
        }

        for (; i < row.Length; i++)
        {
            residual[i] = (byte)(row[i] - topRow[i]);
        }
    }
}
