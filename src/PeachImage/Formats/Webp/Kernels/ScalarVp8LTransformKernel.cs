namespace PeachImage.Formats.Webp.Kernels;

/// <summary>Plain scalar fallback for <see cref="IVp8LTransformKernel"/>, used on hardware with no usable SIMD width and as the tail/remainder loop for the vectorized tiers.</summary>
internal sealed class ScalarVp8LTransformKernel : IVp8LTransformKernel
{
    public void SubtractGreenInverse(Span<uint> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = SubtractGreenInversePixel(pixels[i]);
        }
    }

    public void PredictorTopInverse(Span<byte> row, ReadOnlySpan<byte> topRow)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = (byte)(row[i] + topRow[i]);
        }
    }

    internal static uint SubtractGreenInversePixel(uint argb)
    {
        byte green = (byte)(argb >> 8);
        byte red = (byte)((byte)(argb >> 16) + green);
        byte blue = (byte)((byte)argb + green);
        return (argb & 0xFF00FF00u) | ((uint)red << 16) | blue;
    }
}
