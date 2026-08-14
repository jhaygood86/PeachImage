using System.Diagnostics.CodeAnalysis;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// Selects the inverse/forward DCT kernel to use for this process. Currently always selects
/// <see cref="FastScalarInverseDct"/>/<see cref="FastScalarForwardDct"/> — an exact even/odd fast-DCT
/// factorization (21 multiplies/1D pass vs. the direct definition's 64) that carries no scale-factor risk.
/// Measured (see <c>bench/PeachImage.Benchmarks/DctBenchmarks.cs</c>) to beat every dot-product SIMD tier
/// below despite being scalar: ~19-36% faster per-block than <see cref="Vector256{T}"/> (this selector's
/// previous default on AVX2 hardware), on top of that tier's own multi-x win over the plain scalar
/// reference. The <see cref="Vector256{T}"/>/<see cref="Vector128{T}"/>/scalar dot-product kernels
/// (effectively AVX/AVX2, SSE2/AdvSimd, and a plain double-precision reference) are kept as correctness
/// oracles for <see cref="ScalarInverseDct"/>/<see cref="ScalarForwardDct"/>-relative testing and
/// benchmarking, not reachable from here. TODO: a verified true-AAN kernel (fewer multiplies still, at the
/// cost of needing per-frequency scale factors folded into the quant table — see
/// <see cref="AanScaleFactors"/>, which is ready but not yet consumed by a kernel) may close more of the
/// remaining gap to libjpeg-turbo; point here at that once its butterfly wiring is sourced and verified.
/// </summary>
internal static class DctKernelSelector
{
    /// <summary>The inverse DCT kernel to use for this process.</summary>
    public static IInverseDctKernel Inverse { get; } = SelectInverse();

    /// <summary>The forward DCT kernel to use for this process.</summary>
    public static IForwardDctKernel Forward { get; } = SelectForward();

    [SuppressMessage("Performance", "CA1859", Justification = "Returns IInverseDctKernel by design: the selected concrete kernel is expected to change (SIMD-batched AAN tiers, hardware branches) as this evolves.")]
    private static IInverseDctKernel SelectInverse() => new FastScalarInverseDct();

    [SuppressMessage("Performance", "CA1859", Justification = "Returns IForwardDctKernel by design: the selected concrete kernel is expected to change (SIMD-batched AAN tiers, hardware branches) as this evolves.")]
    private static IForwardDctKernel SelectForward() => new FastScalarForwardDct();
}
