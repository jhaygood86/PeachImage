using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>Selects the fastest available <see cref="IWebpPixelPackKernel"/> for the current hardware at startup, mirroring <see cref="Vp8LTransformKernelSelector"/>'s Vector256 &gt; Vector128 &gt; scalar dispatch.</summary>
internal static class WebpPixelPackKernelSelector
{
    /// <summary>The kernel to use for this process.</summary>
    public static IWebpPixelPackKernel Instance { get; } = Select();

    private static IWebpPixelPackKernel Select()
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return new Vector256WebpPixelPackKernel();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128WebpPixelPackKernel();
        }

        return new ScalarWebpPixelPackKernel();
    }
}
