using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>SIMD tier using <see cref="Vector256{T}"/>'s cross-platform generic static API (JITs to AVX/AVX2 on x86) — 8 pixels/32 bytes at a time.</summary>
internal sealed class Vector256Vp8LTransformKernel : IVp8LTransformKernel
{
    private static readonly Vector256<uint> GreenMask = Vector256.Create(0x0000FF00u);
    private static readonly Vector256<uint> LowChannelMask = Vector256.Create(0x00FF00FFu);
    private static readonly Vector256<uint> HighChannelMask = Vector256.Create(0xFF00FF00u);

    public void SubtractGreenInverse(Span<uint> pixels)
    {
        int n = Vector256<uint>.Count;
        int i = 0;

        for (; i + n <= pixels.Length; i += n)
        {
            var argb = Vector256.Create(pixels.Slice(i, n));
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
        int n = Vector256<byte>.Count;
        int i = 0;

        for (; i + n <= row.Length; i += n)
        {
            var a = Vector256.Create(row.Slice(i, n));
            var b = Vector256.Create(topRow.Slice(i, n));
            (a + b).CopyTo(row.Slice(i, n));
        }

        for (; i < row.Length; i++)
        {
            row[i] = (byte)(row[i] + topRow[i]);
        }
    }
}
