using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1IntraHogPruner"/>'s port of libaom's real HOG-based directional-mode pruning against
/// hand-computed expectations, derived directly from libaom's own algorithm (not just internal consistency).
/// </summary>
public class Av1IntraHogPrunerTests
{
    [Fact]
    public void Predict_ZeroHistogram_ReturnsExactBiasQuantizedToOneOver512()
    {
        // With an all-zero histogram, scores = Bias exactly (before quantization) -- a flat/textureless block
        // has zero gradient everywhere, so this is also what a real, perfectly uniform block's score vector
        // looks like. Expected values hand-computed as (int)(bias * 512 + 0.5) / 512 -- libaom's own
        // av1_nn_output_prec_reduce quantization -- from av1_intra_hog_model_bias's real values.
        Span<float> hist = stackalloc float[32];
        Span<float> scores = stackalloc float[8];

        Av1IntraHogPruner.Predict(hist, scores);

        float[] expected = [231 / 512f, 356 / 512f, -367 / 512f, -327 / 512f, -307 / 512f, -231 / 512f, 29 / 512f, -237 / 512f];
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(expected[i], scores[i], 0.0001f);
        }
    }

    [Fact]
    public void GenerateHog_PureVerticalGradient_SplitsIntoBin0AndBin31()
    {
        // 3x3 block, rows increasing top-to-bottom (10, 20, 30), columns constant -- a pure dy gradient with
        // dx == 0 at the one interior pixel (r=1, c=1). Hand-computed: dx = 0, dy = (30+60+30)-(10+20+10) = 80,
        // temp = 80, total = 0.1 + 80 = 80.1. dx == 0 routes to the special-case split: hist[0] += 40,
        // hist[31] += 40 (libaom's own C `temp / 2` integer division), everything else stays 0.
        int[] source = [10, 10, 10, 20, 20, 20, 30, 30, 30];
        Span<float> hist = stackalloc float[32];

        Av1IntraHogPruner.GenerateHog(source, stride: 3, x: 0, y: 0, width: 3, height: 3, hist);

        float expectedSplit = 40f / 80.1f;
        Assert.Equal(expectedSplit, hist[0], 0.0001f);
        Assert.Equal(expectedSplit, hist[31], 0.0001f);
        for (int i = 1; i < 31; i++)
        {
            Assert.Equal(0f, hist[i]);
        }
    }

    [Fact]
    public void GenerateHog_PureHorizontalGradient_RoutesToBinAtRatioZero()
    {
        // 3x3 block, columns increasing left-to-right (10, 20, 30), rows constant -- a pure dx gradient with
        // dy == 0. Hand-computed: dx = (30+60+30)-(10+20+10) = 80, dy = 0, temp = 80, total = 80.1.
        // GetHistBinIdx(80, 0): ratio = (0 << 16) / 80 = 0 -- the first threshold >= 0 is Thresholds[16] = 3227
        // (Thresholds[15] = -3194 < 0), so this lands in bin 16 with the full, unsplit temp value.
        int[] source = [10, 20, 30, 10, 20, 30, 10, 20, 30];
        Span<float> hist = stackalloc float[32];

        Av1IntraHogPruner.GenerateHog(source, stride: 3, x: 0, y: 0, width: 3, height: 3, hist);

        float expected = 80f / 80.1f;
        Assert.Equal(expected, hist[16], 0.0001f);
        for (int i = 0; i < 32; i++)
        {
            if (i != 16)
            {
                Assert.Equal(0f, hist[i]);
            }
        }
    }

    [Fact]
    public void GenerateHog_FlatBlock_ProducesAllZeroHistogram()
    {
        int[] source = [7, 7, 7, 7, 7, 7, 7, 7, 7];
        Span<float> hist = stackalloc float[32];

        Av1IntraHogPruner.GenerateHog(source, stride: 3, x: 0, y: 0, width: 3, height: 3, hist);

        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(0f, hist[i]);
        }
    }

    [Theory]
    // Exact-boundary cases in the "safe" zone where dx=65536 lets dy equal the fixed-point ratio directly
    // (ratio = dy * 65536 / 65536 = dy exactly) without dy * 65536 overflowing int32 (|dy| must stay well
    // under 2^31/65536 ~ 32768 for that trick to be safe -- true for these mid-table thresholds, not for the
    // table's extreme entries, which use a different dx/dy pair below instead).
    [InlineData(65536, -3194, 15)]
    [InlineData(65536, -3193, 16)]
    [InlineData(65536, 3227, 16)]
    [InlineData(65536, -30982, 11)]
    [InlineData(65536, -30981, 12)]
    // Extreme low end (Thresholds[0] = -1334015): dx=1 keeps dy small (realistic Sobel-output magnitude)
    // while the ratio (dy * 65536) still lands comfortably past this boundary without overflowing.
    [InlineData(1, -21, 0)]
    [InlineData(1, -10, 1)]
    // Extreme high end (Thresholds[30] = 441831, Thresholds[31] = int.MaxValue): dx=100 keeps both dx and dy
    // realistic-sized while landing the truncated ratio just either side of 441831 (674*65536/100 = 441712,
    // 675*65536/100 = 442368) -- and a maximum-plausible-magnitude Sobel dy (1020, the largest possible 3x3
    // Sobel output for 8-bit samples) against dx=1 confirms the top bucket catches genuinely large ratios too.
    [InlineData(100, 674, 30)]
    [InlineData(100, 675, 31)]
    [InlineData(1, 1020, 31)]
    public void GetHistBinIdx_MatchesThresholdBoundaries(int dx, int dy, int expectedBin)
    {
        int bin = Av1IntraHogPruner.GetHistBinIdx(dx, dy);
        Assert.Equal(expectedBin, bin);
    }

    [Fact]
    public void ComputeSkipMask_StrongVerticalEdge_MarksSomeDirectionalModeAtAggressiveThreshold()
    {
        // A real, larger synthetic block (16x16) with a clean, strong vertical edge (left half dark, right
        // half bright) -- enough interior pixels for the histogram to be dominated by one clear direction,
        // not just a single hand-traced pixel. Sanity check only (no independently hand-computed expected
        // score here, unlike the smaller unit tests above): at the most aggressive threshold (0.4, effort>=6),
        // at least one directional mode should be marked prunable, and DC_PRED/SMOOTH*/PAETH_PRED (never
        // touched by this mask) must never be marked regardless.
        const int size = 16;
        var source = new int[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                source[(y * size) + x] = x < size / 2 ? 10 : 250;
            }
        }

        Span<bool> skipMask = stackalloc bool[13];
        Av1IntraHogPruner.ComputeSkipMask(source, size, 0, 0, size, size, threshold: 0.4f, chromaSubsamplingScale: 1, skipMask);

        Assert.False(skipMask[Av1IntraMode.DcPred]);
        Assert.False(skipMask[Av1IntraMode.SmoothPred]);
        Assert.False(skipMask[Av1IntraMode.PaethPred]);

        bool anyDirectionalMarked = false;
        for (int mode = Av1IntraMode.VPred; mode <= Av1IntraMode.D67Pred; mode++)
        {
            anyDirectionalMarked |= skipMask[mode];
        }

        Assert.True(anyDirectionalMarked);
    }
}
