using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Port of libaom's own <c>cfl_test.cc</c> (<c>CFLSubAvgTest</c>/<c>CFLSubsampleLBDTest</c>/
/// <c>CFLPredictTest</c>, unified into one end-to-end check since
/// <see cref="Av1IntraPrediction.PredictChromaFromLuma"/> fuses all three real libaom stages -- subsample,
/// subtract-average, predict -- into a single spec-derived loop rather than exposing them separately):
/// deterministic random luma/chroma/alpha data, run through <see cref="LibaomReferenceCfl.Run"/> (an
/// independent transcription of libaom's own three real C stages) and compared against
/// <see cref="Av1IntraPrediction.PredictChromaFromLuma"/>'s own single-pass output for every chroma
/// subsampling format AVIF actually uses (4:2:0, 4:2:2, 4:4:4) across a representative set of chroma
/// transform sizes.
///
/// <para>Reduced from libaom's own real 100 iterations-per-(size,format) to 200 total draws per
/// (size,format) combination here (a larger number since this single test method covers what three separate
/// libaom test fixtures check) -- input coverage breadth only, not what "correct" means.</para>
/// </summary>
public class Av1CflPredictionTests
{
    public static TheoryData<int, int> Sizes => new()
    {
        { 4, 4 }, { 4, 8 }, { 8, 4 }, { 8, 8 }, { 8, 16 }, { 16, 8 }, { 16, 16 },
    };

    public static TheoryData<int, int> SubsamplingFormats => new()
    {
        { 1, 1 }, // 4:2:0
        { 1, 0 }, // 4:2:2
        { 0, 0 }, // 4:4:4
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void PredictChromaFromLuma_MatchesLibaomReference_420(int w, int h) => Run(w, h, subX: 1, subY: 1);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void PredictChromaFromLuma_MatchesLibaomReference_422(int w, int h) => Run(w, h, subX: 1, subY: 0);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void PredictChromaFromLuma_MatchesLibaomReference_444(int w, int h) => Run(w, h, subX: 0, subY: 0);

    private static void Run(int w, int h, int subX, int subY)
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        const int countTestBlock = 200;

        int log2W = Log2(w);
        int log2H = Log2(h);
        int lumaW = w << subX;
        int lumaH = h << subY;

        for (int it = 0; it < countTestBlock; it++)
        {
            int alpha = rnd.PseudoUniform(33) - 16;
            int dc = rnd.PseudoUniform(256);

            var luma = new int[lumaW * lumaH];
            for (int i = 0; i < luma.Length; i++)
            {
                luma[i] = rnd.PseudoUniform(256);
            }

            var expectedChroma = new int[w * h];
            Array.Fill(expectedChroma, dc);
            LibaomReferenceCfl.Run(luma, lumaW, 0, 0, expectedChroma, alpha, w, h, subX, subY);

            var actualChroma = new int[w * h];
            Array.Fill(actualChroma, dc);
            Av1IntraPrediction.PredictChromaFromLuma(
                actualChroma, w, luma, lumaW, startX: 0, startY: 0, w, h, log2W, log2H,
                subX, subY, alpha, maxLumaW: lumaW, maxLumaH: lumaH, bitDepth: 8);

            for (int k = 0; k < w * h; k++)
            {
                Assert.True(expectedChroma[k] == actualChroma[k], $"({w}x{h}, subX={subX}, subY={subY}), block {it}, position {k}: expected {expectedChroma[k]}, actual {actualChroma[k]}");
            }
        }
    }

    private static int Log2(int n)
    {
        int log = 0;
        while ((1 << log) < n)
        {
            log++;
        }

        return log;
    }
}
