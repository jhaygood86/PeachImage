using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Port of libaom's own <c>av1_k_means_test.cc</c> (<c>AV1KmeansTest1</c>/<c>AV1KmeansTest2</c>
/// <c>CheckOutput</c>): deterministic random pixel data and centroids (matching each real fixture's own
/// <c>SetUp</c> exactly -- dim-1 data is <c>Rand8() &lt;&lt; 4</c>, dim-2 data is plain <c>Rand8()</c>), run
/// through <see cref="Av1PaletteSearch.CalcIndices1D"/>/<c>CalcIndices2D</c> and compared against
/// <see cref="LibaomReferenceKMeans"/> (an independent transcription of libaom's own real
/// <c>av1_calc_indices_dim1_c</c>/<c>dim2_c</c>) for every centroid count libaom's own real test sweeps
/// (2..8, i.e. <c>PALETTE_MIN_SIZE..PALETTE_MAX_SIZE</c>) across a representative subset of block sizes
/// (libaom's own real test sweeps every <c>BLOCK_SIZE</c>; reduced here to four representative pixel
/// counts spanning its range, matching this project's own established reduction discipline for other
/// ported tests).
/// </summary>
public class Av1PaletteKMeansTests
{
    public static TheoryData<int> PixelCounts => new() { 16, 64, 256, 1024 };

    [Theory]
    [MemberData(nameof(PixelCounts))]
    public void CalcIndices1D_MatchesLibaomReference(int n)
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        var data = new short[n];
        for (int i = 0; i < n; i++)
        {
            data[i] = (short)(rnd.Rand8() << 4);
        }

        for (int k = 2; k <= 8; k++)
        {
            var centroids = new short[8];
            for (int i = 0; i < 8; i++)
            {
                centroids[i] = (short)(rnd.Rand8() << 4);
            }

            var expectedIndices = new byte[n];
            long expectedDist = LibaomReferenceKMeans.CalcIndicesDim1(data, centroids, expectedIndices, n, k);

            var dataInt = new int[n];
            for (int i = 0; i < n; i++)
            {
                dataInt[i] = data[i];
            }

            var centroidInt = new int[8];
            for (int i = 0; i < 8; i++)
            {
                centroidInt[i] = centroids[i];
            }

            var actualIndices = new int[n];
            long actualDist = Av1PaletteSearch.CalcIndices1D(dataInt, centroidInt, actualIndices, n, k);

            Assert.Equal(expectedDist, actualDist);
            for (int i = 0; i < n; i++)
            {
                Assert.True(expectedIndices[i] == actualIndices[i], $"n={n}, k={k}, index {i}: expected {expectedIndices[i]}, actual {actualIndices[i]}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(PixelCounts))]
    public void CalcIndices2D_MatchesLibaomReference(int n)
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        var data = new short[n * 2];
        for (int i = 0; i < n * 2; i++)
        {
            data[i] = rnd.Rand8();
        }

        for (int k = 2; k <= 8; k++)
        {
            var centroids = new short[8 * 2];
            for (int i = 0; i < 8 * 2; i++)
            {
                centroids[i] = rnd.Rand8();
            }

            var expectedIndices = new byte[n];
            long expectedDist = LibaomReferenceKMeans.CalcIndicesDim2(data, centroids, expectedIndices, n, k);

            var dataU = new int[n];
            var dataV = new int[n];
            for (int i = 0; i < n; i++)
            {
                dataU[i] = data[i * 2];
                dataV[i] = data[(i * 2) + 1];
            }

            var centroidU = new int[8];
            var centroidV = new int[8];
            for (int i = 0; i < 8; i++)
            {
                centroidU[i] = centroids[i * 2];
                centroidV[i] = centroids[(i * 2) + 1];
            }

            var actualIndices = new int[n];
            long actualDist = Av1PaletteSearch.CalcIndices2D(dataU, dataV, centroidU, centroidV, actualIndices, n, k);

            Assert.Equal(expectedDist, actualDist);
            for (int i = 0; i < n; i++)
            {
                Assert.True(expectedIndices[i] == actualIndices[i], $"n={n}, k={k}, index {i}: expected {expectedIndices[i]}, actual {actualIndices[i]}");
            }
        }
    }
}
