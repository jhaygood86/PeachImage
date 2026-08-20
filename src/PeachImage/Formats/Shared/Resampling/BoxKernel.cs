namespace PeachImage.Formats.Shared.Resampling;

/// <summary>Nearest-pixel averaging filter: uniform weight within a half-pixel radius, zero beyond it.</summary>
internal sealed class BoxKernel : IResamplingKernel
{
    public double Radius => 0.5;

    public double GetWeight(double x) => Math.Abs(x) <= 0.5 ? 1.0 : 0.0;
}
