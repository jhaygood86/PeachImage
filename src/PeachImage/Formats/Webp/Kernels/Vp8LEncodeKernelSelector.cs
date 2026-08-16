using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>Selects the fastest available <see cref="IVp8LEncodeKernel"/> for the current hardware at startup, mirroring <see cref="Vp8LTransformKernelSelector"/>'s Vector256 &gt; Vector128 &gt; scalar dispatch.</summary>
internal static class Vp8LEncodeKernelSelector
{
    /// <summary>The encode kernel to use for this process.</summary>
    public static IVp8LEncodeKernel Instance { get; } = Select();

    private static IVp8LEncodeKernel Select()
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return new Vector256Vp8LEncodeKernel();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128Vp8LEncodeKernel();
        }

        return new ScalarVp8LEncodeKernel();
    }
}
