using System.Diagnostics.CodeAnalysis;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// Selects the inverse/forward DCT kernel to use for this process. Currently always selects
/// <see cref="AanScalarInverseDct"/>/<see cref="AanScalarForwardDct"/> — the true minimal-multiply AAN
/// (Arai-Agui-Nakajima) factorization (5 multiplies/1D pass), verified via matrix cross-check and
/// impulse-response tests independent of both <see cref="ScalarInverseDct"/>/<see cref="ScalarForwardDct"/>
/// and <see cref="AanScaleFactors"/> itself (see
/// <c>tests/.../Unit/Dct/AanScalarDctIndependentVerificationTests.cs</c>). Measured (see
/// <c>bench/PeachImage.Benchmarks/DctBenchmarks.cs</c>) ~17-23% faster per-block than
/// <see cref="FastScalarInverseDct"/>/<see cref="FastScalarForwardDct"/> (this selector's previous default),
/// on top of that tier's own win over the dot-product SIMD kernels below. The <see cref="Vector256{T}"/>/
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

    [SuppressMessage("Performance", "CA1859", Justification = "Returns IInverseDctKernel by design: the selected concrete kernel is expected to change (SIMD-batched AAN tiers, hardware branches) as this evolves.")]
    private static IInverseDctKernel SelectInverse() => new AanScalarInverseDct();

    [SuppressMessage("Performance", "CA1859", Justification = "Returns IForwardDctKernel by design: the selected concrete kernel is expected to change (SIMD-batched AAN tiers, hardware branches) as this evolves.")]
    private static IForwardDctKernel SelectForward() => new AanScalarForwardDct();
}
