namespace PeachImage.Formats.Shared.Resampling;

/// <summary>Windowed-sinc filter <c>sinc(x) * sinc(x / a)</c> for <c>|x| &lt; a</c>, with window radius <paramref name="a"/>.</summary>
internal sealed class LanczosKernel(double a) : IResamplingKernel
{
    public double Radius => a;

    public double GetWeight(double x)
    {
        x = Math.Abs(x);
        return x >= a ? 0.0 : Sinc(x) * Sinc(x / a);
    }

    private static double Sinc(double x)
    {
        if (x == 0.0)
        {
            return 1.0;
        }

        double piX = Math.PI * x;
        return Math.Sin(piX) / piX;
    }
}
