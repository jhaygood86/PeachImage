using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Port of libaom's own <c>rd_test.cc</c> (<c>RdTest.GetDeltaqOffsetValueTest1/2</c>,
/// <c>GetDeltaqOffsetBoundaryTest1/2</c>, <c>GetDeltaqOffsetUnitaryTest1</c>): verifies
/// <see cref="Av1DeltaQ.GetDeltaqOffset"/> against libaom's own real, exhaustive (not randomized -- neither
/// is the real test) test cases exactly, transcribed verbatim.
/// </summary>
public class Av1DeltaQTests
{
    /// <summary><c>GetDeltaqOffsetValueTest1</c>: beta = 4 halves the DC quant step.</summary>
    [Fact]
    public void GetDeltaqOffset_BetaFour_HalvesDcQuantStep()
    {
        const int qindex = 29;
        int dcQStep = Av1Dequantizer.DcQ(qindex, 8);
        Assert.Equal(32, dcQStep);

        int refNewDcQStep = (int)Math.Round(dcQStep / Math.Sqrt(4));
        Assert.Equal(16, refNewDcQStep);

        int deltaQ = Av1DeltaQ.GetDeltaqOffset(8, qindex, 4);
        int newDcQStep = Av1Dequantizer.DcQ(qindex + deltaQ, 8);

        Assert.Equal(refNewDcQStep, newDcQStep);
    }

    /// <summary><c>GetDeltaqOffsetValueTest2</c>: beta = 1/4 doubles the DC quant step.</summary>
    [Fact]
    public void GetDeltaqOffset_BetaQuarter_DoublesDcQuantStep()
    {
        const int qindex = 29;
        double beta = 1.0 / 4.0;
        int dcQStep = Av1Dequantizer.DcQ(qindex, 8);
        Assert.Equal(32, dcQStep);

        int refNewDcQStep = (int)Math.Round(dcQStep / Math.Sqrt(beta));
        Assert.Equal(64, refNewDcQStep);

        int deltaQ = Av1DeltaQ.GetDeltaqOffset(8, qindex, beta);
        int newDcQStep = Av1Dequantizer.DcQ(qindex + deltaQ, 8);

        Assert.Equal(refNewDcQStep, newDcQStep);
    }

    /// <summary><c>GetDeltaqOffsetBoundaryTest1</c>: a near-zero beta (huge sharpening request) clamps to the qindex 255 ceiling, never overflowing.</summary>
    [Theory]
    [InlineData(254)]
    [InlineData(255)]
    public void GetDeltaqOffset_NearZeroBeta_ClampsToMaxQindex(int qindex)
    {
        int deltaQ = Av1DeltaQ.GetDeltaqOffset(8, qindex, 0.000000001);
        Assert.Equal(255, qindex + deltaQ);
    }

    /// <summary><c>GetDeltaqOffsetBoundaryTest2</c>: a large beta (huge blurring request) clamps to the qindex 0 floor, never underflowing.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void GetDeltaqOffset_LargeBeta_ClampsToMinQindex(int qindex)
    {
        int deltaQ = Av1DeltaQ.GetDeltaqOffset(8, qindex, 100);
        Assert.Equal(0, qindex + deltaQ);
    }

    /// <summary><c>GetDeltaqOffsetUnitaryTest1</c>: beta = 1 (no adjustment requested) is a no-op for every real qindex.</summary>
    [Fact]
    public void GetDeltaqOffset_BetaOne_IsAlwaysZero()
    {
        for (int qindex = 0; qindex < 255; qindex++)
        {
            Assert.Equal(0, Av1DeltaQ.GetDeltaqOffset(8, qindex, 1));
        }
    }
}
