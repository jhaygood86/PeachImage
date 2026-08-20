namespace PeachImage.Formats.Shared.Resampling;

/// <summary>Maps a public <see cref="ResamplingFilter"/> to its internal weight-function kernel.</summary>
internal static class ResamplingKernelFactory
{
    // Bicubic and CatmullRom share one instance: Keys' cubic convolution with a = -0.5 is numerically
    // identical to the generalized two-parameter cubic with (B, C) = (0, 0.5) — the two names exist for
    // familiarity (most photo editors call it "Bicubic"; ImageMagick/GIMP call it "Catmull-Rom"), not
    // because they're different filters. See CubicBcKernel's remarks.
    private static readonly CubicBcKernel CatmullRomKernel = new(0, 0.5);

    public static IResamplingKernel Create(ResamplingFilter filter) => filter switch
    {
        ResamplingFilter.Bicubic or ResamplingFilter.CatmullRom => CatmullRomKernel,
        ResamplingFilter.Box => new BoxKernel(),
        ResamplingFilter.Hermite => new CubicBcKernel(0, 0),
        ResamplingFilter.Lanczos2 => new LanczosKernel(2),
        ResamplingFilter.Lanczos3 => new LanczosKernel(3),
        ResamplingFilter.Lanczos5 => new LanczosKernel(5),
        ResamplingFilter.Lanczos8 => new LanczosKernel(8),
        ResamplingFilter.MitchellNetravali => new CubicBcKernel(1.0 / 3.0, 1.0 / 3.0),
        ResamplingFilter.Robidoux => new CubicBcKernel(0.37821576, 0.31089257),
        ResamplingFilter.RobidouxSharp => new CubicBcKernel(0.2620145, 0.3689927),
        ResamplingFilter.Spline => new CubicBcKernel(1, 0),
        ResamplingFilter.Bilinear => new TriangleKernel(),
        ResamplingFilter.Welch => new WelchKernel(),
        ResamplingFilter.NearestNeighbor => throw new ArgumentException(
            $"{nameof(ResamplingFilter.NearestNeighbor)} has no weight-function kernel; it's handled as a dedicated fast path by the caller.",
            nameof(filter)),
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, message: null),
    };
}
