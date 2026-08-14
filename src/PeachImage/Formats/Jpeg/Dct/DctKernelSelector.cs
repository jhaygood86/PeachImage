using System.Diagnostics.CodeAnalysis;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// Selects the inverse/forward DCT kernel to use for this process: <see cref="Vector256AanInverseDct"/>/
/// <see cref="Vector256AanForwardDct"/> when <see cref="Avx.IsSupported"/> (their <see cref="AanVectorButterfly.Transpose8x8"/>
/// needs real AVX intrinsics, not just <see cref="Vector256{T}"/>'s portable API), otherwise
/// <see cref="AanScalarInverseDct"/>/<see cref="AanScalarForwardDct"/> — the true minimal-multiply AAN
/// (Arai-Agui-Nakajima) factorization (5 multiplies/1D pass), verified via matrix cross-check and
/// impulse-response tests independent of both <see cref="ScalarInverseDct"/>/<see cref="ScalarForwardDct"/>
/// and <see cref="AanScaleFactors"/> itself (see
/// <c>tests/.../Unit/Dct/AanScalarDctIndependentVerificationTests.cs</c>). The scalar AAN tier is this
/// process's only fallback (e.g. on ARM64) — no ARM64 SIMD tier exists yet. The <see cref="Vector256{T}"/>/
/// <see cref="Vector128{T}"/>/scalar dot-product kernels and <see cref="FastScalarInverseDct"/>/
/// <see cref="FastScalarForwardDct"/> are kept as correctness oracles for <see cref="ScalarInverseDct"/>/
/// <see cref="ScalarForwardDct"/>-relative testing and benchmarking, not reachable from here.
/// </summary>
internal static class DctKernelSelector
{
    /// <summary>The inverse DCT kernel to use for this process.</summary>
    public static IInverseDctKernel Inverse { get; } = SelectInverse();

    /// <summary>The forward DCT kernel to use for this process.</summary>
    public static IForwardDctKernel Forward { get; } = SelectForward();

    [SuppressMessage("Performance", "CA1859", Justification = "Returns IInverseDctKernel by design: the selected concrete kernel is expected to change (batched AAN tiers, hardware branches) as this evolves.")]
    private static IInverseDctKernel SelectInverse() => Avx.IsSupported ? new Vector256AanInverseDct() : new AanScalarInverseDct();

    [SuppressMessage("Performance", "CA1859", Justification = "Returns IForwardDctKernel by design: the selected concrete kernel is expected to change (batched AAN tiers, hardware branches) as this evolves.")]
    private static IForwardDctKernel SelectForward() => Avx.IsSupported ? new Vector256AanForwardDct() : new AanScalarForwardDct();
}
