using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>SIMD tier using <see cref="Vector256{T}"/>'s cross-platform generic static API (JITs to AVX/AVX2 on x86) — 8 pixels/32 bytes at a time.</summary>
internal sealed class Vector256Vp8LTransformKernel : IVp8LTransformKernel
{
    private static readonly Vector256<uint> GreenMask = Vector256.Create(0x0000FF00u);

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
}
