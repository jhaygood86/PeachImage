using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>SIMD tier using <see cref="Vector128{T}"/>'s cross-platform generic static API (JITs to SSE2 on x86, AdvSimd on Arm) — 4 pixels/16 bytes at a time.</summary>
internal sealed class Vector128Vp8LTransformKernel : IVp8LTransformKernel
{
    private static readonly Vector128<uint> GreenMask = Vector128.Create(0x0000FF00u);
    private static readonly Vector128<uint> LowChannelMask = Vector128.Create(0x00FF00FFu);
    private static readonly Vector128<uint> HighChannelMask = Vector128.Create(0xFF00FF00u);

    public void SubtractGreenInverse(Span<uint> pixels)
    {
        int n = Vector128<uint>.Count;
        int i = 0;

        for (; i + n <= pixels.Length; i += n)
        {
            var argb = Vector128.Create(pixels.Slice(i, n));
            var green = (argb & GreenMask) >> 8;
            var redBlueAdd = (green << 16) | green;
            var newRedBlue = (argb + redBlueAdd) & LowChannelMask;
            var result = (argb & HighChannelMask) | newRedBlue;
            result.CopyTo(pixels.Slice(i, n));
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
            var a = Vector128.Create(row.Slice(i, n));
            var b = Vector128.Create(topRow.Slice(i, n));
            (a + b).CopyTo(row.Slice(i, n));
        }

        for (; i < row.Length; i++)
        {
            row[i] = (byte)(row[i] + topRow[i]);
        }
    }
}
