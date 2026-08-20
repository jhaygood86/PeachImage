namespace PeachImage.Formats.Shared.Resampling;

/// <summary>Linear interpolation filter (the "tent"/triangle function), radius 1 — backs <see cref="ResamplingFilter.Bilinear"/>.</summary>
internal sealed class TriangleKernel : IResamplingKernel
{
    public double Radius => 1.0;

    public double GetWeight(double x)
    {
        x = Math.Abs(x);
        return x < 1.0 ? 1.0 - x : 0.0;
    }
}
