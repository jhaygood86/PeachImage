namespace PeachImage.Formats.Shared.Resampling;

/// <summary>A 1D resampling filter's weight function, evaluated over <c>[-Radius, Radius]</c>.</summary>
internal interface IResamplingKernel
{
    /// <summary>How far from the sample center (in source-pixel units) this kernel has nonzero weight.</summary>
    double Radius { get; }

    /// <summary>The kernel's weight at offset <paramref name="x"/> (in source-pixel units) from the sample center.</summary>
    double GetWeight(double x);
}
