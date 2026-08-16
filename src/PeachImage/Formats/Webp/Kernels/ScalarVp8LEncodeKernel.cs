namespace PeachImage.Formats.Webp.Kernels;

/// <summary>Plain scalar fallback for <see cref="IVp8LEncodeKernel"/>, used on hardware with no usable SIMD width and as the tail/remainder loop for the vectorized tiers.</summary>
internal sealed class ScalarVp8LEncodeKernel : IVp8LEncodeKernel
{
    public void SubtractGreenForward(Span<uint> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = SubtractGreenForwardPixel(pixels[i]);
        }
    }

    public void PredictorTopForward(ReadOnlySpan<byte> row, ReadOnlySpan<byte> topRow, Span<byte> residual)
    {
        for (int i = 0; i < row.Length; i++)
        {
            residual[i] = (byte)(row[i] - topRow[i]);
        }
    }

    internal static uint SubtractGreenForwardPixel(uint argb)
    {
        byte green = (byte)(argb >> 8);
        byte red = (byte)((byte)(argb >> 16) - green);
        byte blue = (byte)((byte)argb - green);
        return (argb & 0xFF00FF00u) | ((uint)red << 16) | blue;
    }
}
