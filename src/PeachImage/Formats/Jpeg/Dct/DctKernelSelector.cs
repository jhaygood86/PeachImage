using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// Selects the fastest available inverse/forward DCT kernel for the current hardware at startup:
/// <see cref="Vector256{T}"/> generic intrinsics (effectively AVX/AVX2) when hardware-accelerated, else
/// <see cref="Vector128{T}"/> generic intrinsics (SSE2 on x86, AdvSimd on Arm — one source file covers
/// both), else the scalar double-precision reference kernel.
/// </summary>
internal static class DctKernelSelector
{
    /// <summary>The inverse DCT kernel to use for this process.</summary>
    public static IInverseDctKernel Inverse { get; } = SelectInverse();

    /// <summary>The forward DCT kernel to use for this process.</summary>
    public static IForwardDctKernel Forward { get; } = SelectForward();

    private static IInverseDctKernel SelectInverse()
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return new Vector256InverseDct();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128InverseDct();
        }

        return new ScalarInverseDct();
    }

    private static IForwardDctKernel SelectForward()
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return new Vector256ForwardDct();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128ForwardDct();
        }

        return new ScalarForwardDct();
    }
}
