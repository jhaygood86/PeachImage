using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>Selects the fastest available <see cref="IVp8LTransformKernel"/> for the current hardware at startup, mirroring <c>Jpeg.ColorConversion.ColorConverterSelector</c>'s Vector256 &gt; Vector128 &gt; scalar dispatch.</summary>
internal static class Vp8LTransformKernelSelector
{
    /// <summary>The transform kernel to use for this process.</summary>
    public static IVp8LTransformKernel Instance { get; } = Select();

    private static IVp8LTransformKernel Select()
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return new Vector256Vp8LTransformKernel();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128Vp8LTransformKernel();
        }

        return new ScalarVp8LTransformKernel();
    }
}
