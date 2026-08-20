using PeachImage.Formats.Shared.Resampling;

namespace PeachImage.Tests.Resizing;

/// <summary>
/// Landmark-value and symmetry checks for each <see cref="IResamplingKernel"/> — cheap to verify
/// independently of the resize pipeline, and the first place a sign or coefficient slip in a filter formula
/// would show up (as it did during development: <see cref="CubicBcKernel"/>'s <c>1 &lt;= |x| &lt; 2</c> branch
/// originally used the wrong linear coefficient, which this class's boundary-value checks below would have
/// caught directly instead of only showing up as a diffuse quality regression downstream).
/// </summary>
public class KernelWeightFunctionTests
{
    public static IEnumerable<object[]> AllFilters()
    {
        foreach (ResamplingFilter filter in Enum.GetValues<ResamplingFilter>())
        {
            if (filter != ResamplingFilter.NearestNeighbor)
            {
                yield return [filter];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllFilters))]
    public void IsEven_WeightAtNegativeX_MatchesPositiveX(ResamplingFilter filter)
    {
        var kernel = ResamplingKernelFactory.Create(filter);
        double[] samples = [0.1, 0.37, 0.5, 0.9, 1.0, 1.3, 1.9, 2.0, 3.7, 7.9];
        foreach (double x in samples)
        {
            Assert.True(Math.Abs(kernel.GetWeight(x) - kernel.GetWeight(-x)) < 1e-10, $"{filter} at x={x}");
        }
    }

    [Theory]
    [MemberData(nameof(AllFilters))]
    public void IsZero_BeyondRadius(ResamplingFilter filter)
    {
        // Box is the one exception: its weight function is deliberately inclusive at exactly the radius
        // (see BoxKernel_IsFlatWithinHalfPixelRadius below) so a window always has nonzero width — every
        // other kernel here tapers to exactly zero at its radius, checked separately.
        var kernel = ResamplingKernelFactory.Create(filter);
        if (filter != ResamplingFilter.Box)
        {
            Assert.True(Math.Abs(kernel.GetWeight(kernel.Radius)) < 1e-6, filter.ToString());
        }

        Assert.Equal(0.0, kernel.GetWeight(kernel.Radius + 0.5));
        Assert.Equal(0.0, kernel.GetWeight(kernel.Radius * 10));
    }

    [Theory]
    [InlineData(ResamplingFilter.Bicubic, 1.0)] // Keys a=-0.5 == CatmullRom (B=0, C=0.5): interpolating, weight(0) = 1.
    [InlineData(ResamplingFilter.CatmullRom, 1.0)]
    [InlineData(ResamplingFilter.Hermite, 1.0)] // (B=0, C=0): also interpolating.
    [InlineData(ResamplingFilter.MitchellNetravali, 8.0 / 9.0)] // (B=1/3, C=1/3): weight(0) = 1 - B/3.
    [InlineData(ResamplingFilter.Spline, 2.0 / 3.0)] // (B=1, C=0): weight(0) = 1 - B/3.
    [InlineData(ResamplingFilter.Box, 1.0)]
    [InlineData(ResamplingFilter.Bilinear, 1.0)]
    [InlineData(ResamplingFilter.Welch, 1.0)]
    [InlineData(ResamplingFilter.Lanczos3, 1.0)]
    public void WeightAtZero_MatchesKnownValue(ResamplingFilter filter, double expected)
    {
        var kernel = ResamplingKernelFactory.Create(filter);
        Assert.Equal(expected, kernel.GetWeight(0.0), precision: 6);
    }

    [Theory]
    [InlineData(ResamplingFilter.Bicubic)] // Interpolating cubics (B=0) reproduce sample points exactly:
    [InlineData(ResamplingFilter.CatmullRom)] // weight is 0 at every nonzero integer offset within the radius.
    [InlineData(ResamplingFilter.Hermite)]
    public void InterpolatingCubic_WeightAtOne_IsZero(ResamplingFilter filter)
    {
        var kernel = ResamplingKernelFactory.Create(filter);
        Assert.Equal(0.0, kernel.GetWeight(1.0), precision: 6);
    }

    [Fact]
    public void CubicBcKernel_IsContinuousAtPieceBoundary()
    {
        // The two-piece formula's pieces must agree at x = 1 for every (B, C) this repo uses — this is what
        // the coefficient bug (see the type-level remarks above) broke for every kernel with C != 0.
        (double b, double c)[] cases = [(0, 0.5), (0, 0), (1.0 / 3, 1.0 / 3), (1, 0), (0.37821576, 0.31089257), (0.2620145, 0.3689927)];
        foreach (var (b, c) in cases)
        {
            var kernel = new CubicBcKernel(b, c);
            double justBelow = kernel.GetWeight(0.999999);
            double justAbove = kernel.GetWeight(1.000001);
            Assert.True(Math.Abs(justBelow - justAbove) < 1e-4, $"(B={b}, C={c}): {justBelow} vs {justAbove}");
        }
    }

    [Fact]
    public void LanczosKernel_WeightAtZero_IsOne()
    {
        foreach (double a in new double[] { 2, 3, 5, 8 })
        {
            Assert.Equal(1.0, new LanczosKernel(a).GetWeight(0.0), precision: 6);
        }
    }

    [Fact]
    public void BoxKernel_IsFlatWithinHalfPixelRadius()
    {
        var box = new BoxKernel();
        Assert.Equal(1.0, box.GetWeight(0.0));
        Assert.Equal(1.0, box.GetWeight(0.5));
        Assert.Equal(0.0, box.GetWeight(0.500001));
    }

    [Fact]
    public void TriangleKernel_IsLinearRamp()
    {
        var triangle = new TriangleKernel();
        Assert.Equal(1.0, triangle.GetWeight(0.0));
        Assert.Equal(0.5, triangle.GetWeight(0.5));
        Assert.Equal(0.0, triangle.GetWeight(1.0));
    }
}
