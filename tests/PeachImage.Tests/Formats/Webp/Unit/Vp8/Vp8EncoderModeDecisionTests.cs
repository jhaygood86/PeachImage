using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// The mode-decision SAD and the whole-block TM predictor run on <see cref="System.Runtime.Intrinsics.Vector128"/>
/// where available. These check them against plain scalar definitions, so a divergence shows up here rather than
/// as a silently different (but still decodable) encode.
/// </summary>
public class Vp8EncoderModeDecisionTests
{
    private const int Stride = 40;
    private const int Rows = 40;
    private const int Origin = (1 * Stride) + 1;

    private static byte[] RandomPlane(int seed, bool extremes)
    {
        var plane = new byte[Stride * Rows];
        var rng = new Random(seed);
        rng.NextBytes(plane);
        if (extremes)
        {
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] = (i & 1) == 0 ? (byte)0 : (byte)255;
            }
        }

        return plane;
    }

    private static int ScalarSad(byte[] a, byte[] b, int size)
    {
        int sad = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                sad += Math.Abs(a[Origin + (y * Stride) + x] - b[Origin + (y * Stride) + x]);
            }
        }

        return sad;
    }

    [Theory]
    [InlineData(16, 1, false)]
    [InlineData(16, 2, true)]
    [InlineData(8, 3, false)]
    [InlineData(8, 4, true)]
    public void WholeBlockMode_ReportsTheScalarSadOfTheCommittedPrediction(int size, int seed, bool extremes)
    {
        var source = RandomPlane(seed, extremes);
        var recon = RandomPlane(seed + 100, extremes);

        int mode = Vp8ModeDecision.SelectWholeBlockMode(recon, Origin, Stride, size, hasAbove: true, hasLeft: true, source, Origin, Stride, out int sad);

        // The block left in recon must be the winning mode's prediction, and sad must be its scalar SAD.
        Assert.Equal(ScalarSad(source, recon, size), sad);

        // And no other mode may beat it (strictly lower SAD) -- re-predict each candidate and compare.
        foreach (int other in new[] { Vp8PredictionModes.DcPred, Vp8PredictionModes.VPred, Vp8PredictionModes.HPred, Vp8PredictionModes.TmPred })
        {
            var scratch = (byte[])recon.Clone();
            Vp8IntraPredictionWholeBlock.PredictModeWholeBlock(other, scratch, Origin, Stride, size, true, true);
            Assert.True(ScalarSad(source, scratch, size) >= sad, $"mode {other} beats the chosen mode {mode}");
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void SubblockMode_ReportsTheScalarSadOfTheCommittedPrediction(int seed, bool extremes)
    {
        var source = RandomPlane(seed, extremes);
        var recon = RandomPlane(seed + 50, extremes);
        byte[] aboveRight = [1, 200, 3, 255];

        Vp8ModeDecision.SelectSubblockMode(recon, Origin, Stride, aboveRight, source, Origin, Stride, out int sad);

        Assert.Equal(ScalarSad(source, recon, 4), sad);
    }

    [Theory]
    [InlineData(16, 1, false)]
    [InlineData(16, 2, true)]
    [InlineData(8, 3, false)]
    [InlineData(8, 4, true)]
    [InlineData(4, 5, false)]
    public void TrueMotion_MatchesPerPixelClip(int size, int seed, bool extremes)
    {
        var plane = RandomPlane(seed, extremes);
        var expected = (byte[])plane.Clone();

        int corner = expected[Origin - Stride - 1];
        for (int y = 0; y < size; y++)
        {
            int left = expected[Origin + (y * Stride) - 1];
            for (int x = 0; x < size; x++)
            {
                int above = expected[Origin - Stride + x];
                expected[Origin + (y * Stride) + x] = (byte)Math.Clamp(above + left - corner, 0, 255);
            }
        }

        Vp8IntraPredictionWholeBlock.PredictTrueMotion(plane, Origin, Stride, size);

        Assert.Equal(expected, plane);
    }
}
