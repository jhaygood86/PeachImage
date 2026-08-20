namespace PeachImage.Formats.Shared.Resampling;

/// <summary>
/// The generalized two-parameter cubic convolution filter (Mitchell &amp; Netravali, 1988), radius 2.
/// Different <c>(B, C)</c> pairs reproduce several distinctly-named filters — Catmull-Rom (0, 0.5), Hermite
/// (0, 0), Mitchell-Netravali (1/3, 1/3), Spline (1, 0), and the Robidoux family's numerically-derived
/// constants — so they're all implemented here as one formula rather than several near-duplicate ones. Keys'
/// cubic convolution with a = -0.5 (what most photo editors call "Bicubic") is numerically identical to
/// (B, C) = (0, 0.5), so <see cref="ResamplingKernelFactory"/> reuses this same kernel for both names.
/// </summary>
internal sealed class CubicBcKernel(double b, double c) : IResamplingKernel
{
    public double Radius => 2.0;

    public double GetWeight(double x)
    {
        x = Math.Abs(x);
        double x2 = x * x;
        double x3 = x2 * x;

        if (x < 1.0)
        {
            return (((12 - (9 * b) - (6 * c)) * x3) + ((-18 + (12 * b) + (6 * c)) * x2) + (6 - (2 * b))) / 6.0;
        }

        if (x < 2.0)
        {
            return ((((-b) - (6 * c)) * x3) + (((6 * b) + (30 * c)) * x2) + ((((-12) * b) - (48 * c)) * x) + ((8 * b) + (24 * c))) / 6.0;
        }

        return 0.0;
    }
}
