using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1ForwardQuantizer"/> and <see cref="Av1LocalReconstructor"/> by driving the full
/// forward-transform -&gt; quantize -&gt; dequantize -&gt; inverse-transform -&gt; reconstruct pipeline and
/// checking reconstruction error stays within the quantizer's own expected step size.
/// </summary>
public class Av1QuantizerAndReconstructorTests
{
    [Theory]
    [InlineData(100, 1)]
    [InlineData(75, 64)]
    [InlineData(50, 128)]
    [InlineData(0, 255)]
    public void QualityToBaseQIdx_MapsExpectedEndpointsAndMidpoint(int quality, int expectedBaseQIdx)
    {
        Assert.Equal(expectedBaseQIdx, Av1ForwardQuantizer.QualityToBaseQIdx(quality));
    }

    /// <summary>
    /// Independent transcription of the real formula chain <c>QualityToBaseQIdx</c> ports (libavif's own
    /// <c>aomQualityToQuantizer</c> default branch, <c>src/codec_aom.c</c>, feeding libaom's own real
    /// <c>quantizer_to_qindex</c> lookup table, <c>av1/encoder/av1_quantize.c</c>) -- checked across the
    /// full 0-100 quality range, not just the three round-number spot checks above.
    /// </summary>
    [Fact]
    public void QualityToBaseQIdx_MatchesLibavifAndLibaomReferenceFormula()
    {
        int[] quantizerToQindex =
        [
            0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48,
            52, 56, 60, 64, 68, 72, 76, 80, 84, 88, 92, 96, 100,
            104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 148, 152,
            156, 160, 164, 168, 172, 176, 180, 184, 188, 192, 196, 200, 204,
            208, 212, 216, 220, 224, 228, 232, 236, 240, 244, 249, 255,
        ];

        for (int quality = 0; quality <= 100; quality++)
        {
            int quantizer = (((100 - quality) * 63) + 50) / 100;
            int expected = Math.Max(quantizerToQindex[quantizer], 1);

            Assert.Equal(expected, Av1ForwardQuantizer.QualityToBaseQIdx(quality));
        }
    }

    [Fact]
    public void QualityToBaseQIdx_IsMonotonicallyNonIncreasingAsQualityIncreases()
    {
        int previous = Av1ForwardQuantizer.QualityToBaseQIdx(0);
        for (int quality = 1; quality <= 100; quality++)
        {
            int current = Av1ForwardQuantizer.QualityToBaseQIdx(quality);
            Assert.InRange(current, 1, 255);
            Assert.True(current <= previous, $"quality {quality}: baseQIdx {current} should be <= previous {previous}.");
            previous = current;
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Quantize_ZeroCoefficients_ProducesZeroLevels(int size)
    {
        int[] coeff = new int[size * size];
        int[] levels = new int[size * size];

        Av1ForwardQuantizer.Quantize(coeff, levels, size, baseQIdx: 64);

        Assert.All(levels, level => Assert.Equal(0, level));
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(4, 32)]
    [InlineData(4, 255)]
    [InlineData(8, 64)]
    [InlineData(16, 64)]
    [InlineData(32, 64)]
    public void FullPipeline_ConstantResidual_ReconstructsWithinQuantizerStepTolerance(int size, int baseQIdx)
    {
        int[] residual = new int[size * size];
        Array.Fill(residual, 40);

        AssertReconstructsWithinTolerance(residual, size, baseQIdx);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(8, 32)]
    [InlineData(16, 64)]
    [InlineData(32, 128)]
    public void FullPipeline_RandomResidual_ReconstructsWithinQuantizerStepTolerance(int size, int baseQIdx)
    {
        var random = new Random(999);
        int[] residual = new int[size * size];
        for (int i = 0; i < residual.Length; i++)
        {
            residual[i] = random.Next(-100, 100);
        }

        AssertReconstructsWithinTolerance(residual, size, baseQIdx);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(32)]
    public void FullPipeline_FinestQuantizer_ReconstructsNearExactly(int size)
    {
        // baseQIdx = 1 is the finest available quantizer step (dc_q/ac_q = 4 at 8-bit) -- reconstruction
        // should be very close to the original residual.
        int[] residual = new int[size * size];
        for (int i = 0; i < residual.Length; i++)
        {
            residual[i] = (i % 17) - 8;
        }

        AssertReconstructsWithinTolerance(residual, size, baseQIdx: 1, toleranceOverride: 6);
    }

    private static void AssertReconstructsWithinTolerance(int[] residual, int size, int baseQIdx, int? toleranceOverride = null)
    {
        int[] coeff = new int[size * size];
        Av1ForwardTransform.Forward2D(residual, coeff, size);

        int[] levels = new int[size * size];
        Av1ForwardQuantizer.Quantize(coeff, levels, size, baseQIdx);

        // A mid-gray (128) prediction baseline, not zero -- Reconstruct clamps to the valid [0, 255] pixel
        // range, so a zero baseline would clip every negative residual to 0 regardless of quantization
        // fidelity, which is a property of 8-bit reconstruction, not something this test should be
        // measuring. Expected output is therefore Clamp(128 + residual, 0, 255), not the raw residual.
        const int predictionBaseline = 128;
        int[] plane = new int[size * size];
        Array.Fill(plane, predictionBaseline);
        int[] dequantScratch = new int[64 * 64];
        int[] residualScratch = new int[size * size];
        Av1LocalReconstructor.Reconstruct(plane, planeStride: size, x: 0, y: 0, size, levels, baseQIdx, dequantScratch, residualScratch);

        // Quantization is genuinely lossy by design -- bound the error by a generous multiple of the
        // quantizer's own AC step (coarser baseQIdx => larger acceptable error), not an exact match.
        int acQ = PeachImage.Formats.Avif.Decoding.Av1.Av1QuantLookup.AcQLookup[0][baseQIdx];
        int tolerance = toleranceOverride ?? Math.Max(10, acQ);

        for (int i = 0; i < residual.Length; i++)
        {
            int expected = Math.Clamp(predictionBaseline + residual[i], 0, 255);
            Assert.True(
                Math.Abs(plane[i] - expected) <= tolerance,
                $"Index {i}: expected ~{expected} (baseline {predictionBaseline} + residual {residual[i]}), got {plane[i]} (size {size}, baseQIdx {baseQIdx}, tolerance {tolerance}).");
        }
    }
}
