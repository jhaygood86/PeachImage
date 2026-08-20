namespace PeachImage.Formats.Shared.Resampling;

/// <summary>Sinc windowed by the Welch (parabolic) window, radius 1.</summary>
internal sealed class WelchKernel : IResamplingKernel
{
    public double Radius => 1.0;

    public double GetWeight(double x)
    {
        if (x == 0.0)
        {
            return 1.0;
        }

        x = Math.Abs(x);
        if (x >= 1.0)
        {
            return 0.0;
        }

        double piX = Math.PI * x;
        return (Math.Sin(piX) / piX) * (1.0 - (x * x));
    }
}
