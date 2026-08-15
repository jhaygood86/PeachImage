using PeachImage.Formats.Webp.Kernels;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Inverts VP8L's subtract-green transform: <c>red += green; blue += green;</c> (mod 256, no clamp) for
/// every pixel. Has no cross-pixel dependency at all — the simplest and safest SIMD win in the whole
/// decoder — so this just delegates straight to the shared hardware-tier kernel.
/// </summary>
internal static class Vp8LSubtractGreenTransform
{
    public static void ApplyInverse(Span<uint> pixels) => Vp8LTransformKernelSelector.Instance.SubtractGreenInverse(pixels);
}
