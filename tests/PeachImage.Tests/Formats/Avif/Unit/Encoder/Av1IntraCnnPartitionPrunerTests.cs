using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

public sealed class Av1IntraCnnPartitionPrunerTests
{
    [Fact]
    public void Conv2DValidRelu_SumsFilterTapsWithBiasAndAppliesRelu()
    {
        // 1 in-channel, 3x3 input, 1 out-channel, 2x2 filter, stride 1 -> 2x2 output.
        float[] input = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        float[] weights = [1, 1, 1, 1];
        float[] bias = [0];
        var output = new float[4];

        Av1IntraCnnPartitionPruner.Conv2DValidRelu(input, 1, 3, 3, weights, bias, 1, 2, 1, output, 2, 2);

        Assert.Equal(12f, output[0]); // 1+2+4+5
        Assert.Equal(16f, output[1]); // 2+3+5+6
        Assert.Equal(24f, output[2]); // 4+5+7+8
        Assert.Equal(28f, output[3]); // 5+6+8+9
    }

    [Fact]
    public void Conv2DValidRelu_NegativeSumClampsToZero()
    {
        float[] input = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        float[] weights = [1, 1, 1, 1];
        float[] bias = [-20];
        var output = new float[4];

        Av1IntraCnnPartitionPruner.Conv2DValidRelu(input, 1, 3, 3, weights, bias, 1, 2, 1, output, 2, 2);

        Assert.Equal(0f, output[0]); // 12 - 20 = -8 -> ReLU -> 0
        Assert.Equal(0f, output[1]); // 16 - 20 = -4 -> ReLU -> 0
        Assert.Equal(4f, output[2]); // 24 - 20 = 4
        Assert.Equal(8f, output[3]); // 28 - 20 = 8
    }

    [Fact]
    public void Conv2DValidRelu_TwoInputChannelsSumAcrossChannels()
    {
        // 2 in-channels, 2x2 input each, 1 out-channel, 2x2 filter (covers the whole input), stride 1 -> 1x1 output.
        // Channel-major input: channel 0 = [1,2,3,4], channel 1 = [10,20,30,40].
        float[] input = [1, 2, 3, 4, 10, 20, 30, 40];
        // Weight layout: [filterRow][filterCol][inChannel][outChannel], outChannel fastest (1 here), so this is
        // just [filterRow][filterCol][inChannel] flattened: for each of the 4 taps, channel-0 weight then channel-1 weight.
        float[] weights = [1, 0, 1, 0, 1, 0, 1, 0]; // only channel 0 contributes (channel-1 weights are 0)
        float[] bias = [0];
        var output = new float[1];

        Av1IntraCnnPartitionPruner.Conv2DValidRelu(input, 2, 2, 2, weights, bias, 1, 2, 1, output, 1, 1);

        Assert.Equal(10f, output[0]); // 1+2+3+4 from channel 0; channel 1 contributes 0
    }

    [Fact]
    public void PredictMlp_MatchesHandComputedForwardPass()
    {
        // A single-feature input routed through exactly one node per hidden layer, hand-traceable end to end:
        // h0[0] = ReLU(1.0*2.0 + 0.5) = 2.5, h1[0] = ReLU(2.0*2.5 + 1.0) = 6.0, logit = 0.5*6.0 + 0.25 = 3.25
        // (an exact multiple of 1/512, so the precision-reduce step is a no-op here).
        float[] features = [2.0f];
        var layer0Weights = new float[16];
        layer0Weights[0] = 1.0f;
        var layer0Bias = new float[16];
        layer0Bias[0] = 0.5f;

        var layer1Weights = new float[24 * 16];
        layer1Weights[0] = 2.0f; // node 0 of hidden1, weight for h0[0]
        var layer1Bias = new float[24];
        layer1Bias[0] = 1.0f;

        var logitsWeights = new float[24];
        logitsWeights[0] = 0.5f;
        float[] logitsBias = [0.25f];

        float logit = Av1IntraCnnPartitionPruner.PredictMlp(features, 1, layer0Weights, layer0Bias, layer1Weights, layer1Bias, logitsWeights, logitsBias);

        Assert.Equal(3.25f, logit);
    }

    [Fact]
    public void PredictMlp_PrecisionReduceQuantizesToNearest1Over512()
    {
        // logit = 0.003 (no hidden-layer contribution: all weights zero, only the logits bias fires) is not a
        // multiple of 1/512, so av1_nn_output_prec_reduce's own (int)(x*512+0.5) truncating cast must round
        // it: 0.003*512 = 1.536, +0.5 = 2.036, (int) = 2, so the real output is 2/512 = 0.00390625 -- not the
        // original 0.003, confirming the quantization step actually runs (not a no-op check).
        float[] features = [0.0f];
        var layer0Weights = new float[16];
        var layer0Bias = new float[16];
        var layer1Weights = new float[24 * 16];
        var layer1Bias = new float[24];
        var logitsWeights = new float[24];
        float[] logitsBias = [0.003f];

        float logit = Av1IntraCnnPartitionPruner.PredictMlp(features, 1, layer0Weights, layer0Bias, layer1Weights, layer1Bias, logitsWeights, logitsBias);

        Assert.Equal(2f / 512f, logit);
    }

    [Fact]
    public void RunCnnAndGetPruneDecision_ProducesValidDecisionForFlatSyntheticImage()
    {
        // A flat 128x128 gray image has zero luma AC content everywhere -- not a hand-verified expected
        // decision against the real trained weights (treated as a black box here, matching this port's own
        // acceptance that this is a learned model, not a formula), just an end-to-end smoke test confirming
        // the real production weight tables load, the conv/MLP pipeline runs without throwing for every one
        // of the 4 bsizes, and the resulting flags are always internally consistent (at least one partition
        // type survives).
        var source = new int[128 * 128];
        Array.Fill(source, 128);
        var cache = new Av1IntraCnnPartitionPruner.Cache();

        Av1IntraCnnPartitionPruner.RunCnn(source, 128, 128, 128, 0, 0, baseQIdx: 0, bitDepth: 8, cache);
        Assert.True(cache.Populated);

        foreach (int sizeMi in new[] { 16, 8, 4, 2 })
        {
            int quadTreeIdx = sizeMi switch
            {
                16 => 0,
                8 => 1,
                4 => 5,
                _ => 21,
            };

            Av1IntraCnnPartitionPruner.GetPruneDecision(cache, quadTreeIdx, sizeMi, level: 2, minFrameDimensionPixels: 128, out bool noneAllowed, out bool splitAllowed, out bool rectAllowed);

            Assert.True(noneAllowed || splitAllowed || rectAllowed);
        }
    }
}
