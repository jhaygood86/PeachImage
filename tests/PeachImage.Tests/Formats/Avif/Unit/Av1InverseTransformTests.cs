using System.Reflection;
using System.Runtime.Intrinsics;

using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Verifies <see cref="Av1InverseTransform"/> (accessed via reflection since <c>Inverse2D</c> is the only
/// public entry point and its supporting math is private) against a property that's true independent of
/// this implementation: for any pure-DCT transform (<c>DCT_DCT</c>), a single nonzero DC coefficient
/// (position (0,0)) must produce a perfectly flat output block, since the DCT's own DC basis function is
/// constant. This is a cheap, high-confidence sanity check on the row/column butterfly network transcribed
/// from spec §7.13.2 -- any error in the butterfly wiring that broke the DC basis's flatness (a
/// transposition, a wrong angle, a dropped step) would very likely also break every other coefficient
/// pattern, so this is a strong "the network is fundamentally wired correctly" signal even though it can't
/// prove every AC coefficient path bit-exact on its own.
/// </summary>
public class Av1InverseTransformTests
{
    private static readonly MethodInfo Inverse2DMethod = typeof(Av1InverseTransform)
        .GetMethod("Inverse2D", BindingFlags.Public | BindingFlags.Static)!;

    private static int[] Inverse2D(int[] dequant, int txSz, int planeTxType, bool lossless, int bitDepth)
    {
        var residual = new int[64 * 64];
        Inverse2DMethod.Invoke(null, [dequant, residual, txSz, planeTxType, lossless, bitDepth]);
        return residual;
    }

    [Theory]
    [InlineData(Av1TxSize.Tx4x4, 4, 4)]
    [InlineData(Av1TxSize.Tx8x8, 8, 8)]
    [InlineData(Av1TxSize.Tx16x16, 16, 16)]
    [InlineData(Av1TxSize.Tx32x32, 32, 32)]
    [InlineData(Av1TxSize.Tx8x4, 8, 4)]
    [InlineData(Av1TxSize.Tx4x8, 4, 8)]
    [InlineData(Av1TxSize.Tx16x8, 16, 8)]
    [InlineData(Av1TxSize.Tx8x16, 8, 16)]
    public void Inverse2D_DctDct_DcOnlyImpulse_ProducesFlatBlock(int txSz, int w, int h)
    {
        var dequant = new int[64 * 64];
        dequant[0] = 4096;

        int[] residual = Inverse2D(dequant, txSz, Av1TxType.DctDct, lossless: false, bitDepth: 8);

        int expected = residual[0];
        for (int i = 0; i < h; i++)
        {
            for (int j = 0; j < w; j++)
            {
                Assert.Equal(expected, residual[(i * w) + j]);
            }
        }

        Assert.NotEqual(0, expected);
    }

    [Fact]
    public void Inverse2D_DctDct_AllZeroInput_ProducesAllZeroOutput()
    {
        var dequant = new int[64 * 64];
        int[] residual = Inverse2D(dequant, Av1TxSize.Tx16x16, Av1TxType.DctDct, lossless: false, bitDepth: 8);

        for (int i = 0; i < 16 * 16; i++)
        {
            Assert.Equal(0, residual[i]);
        }
    }

    [Theory]
    [InlineData(Av1TxType.AdstAdst)]
    [InlineData(Av1TxType.AdstDct)]
    [InlineData(Av1TxType.DctAdst)]
    [InlineData(Av1TxType.Idtx)]
    [InlineData(Av1TxType.VDct)]
    [InlineData(Av1TxType.HDct)]
    public void Inverse2D_EveryTxType_AllZeroInput_ProducesAllZeroOutput(int planeTxType)
    {
        var dequant = new int[64 * 64];
        int[] residual = Inverse2D(dequant, Av1TxSize.Tx8x8, planeTxType, lossless: false, bitDepth: 8);

        for (int i = 0; i < 8 * 8; i++)
        {
            Assert.Equal(0, residual[i]);
        }
    }

    /// <summary>The lossless path (<c>WHT</c>) is also a linear transform with an all-zero fixed point.</summary>
    [Fact]
    public void Inverse2D_Lossless_AllZeroInput_ProducesAllZeroOutput()
    {
        var dequant = new int[64 * 64];
        int[] residual = Inverse2D(dequant, Av1TxSize.Tx4x4, Av1TxType.DctDct, lossless: true, bitDepth: 8);

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(0, residual[i]);
        }
    }

    /// <summary>
    /// Direct differential check of the 4-row-batched SIMD path (<see cref="Av1InverseTransform.InverseDctBatch"/>/
    /// <c>InverseAdstBatch</c>/<c>InverseIdentityBatch</c>, added to vectorize <c>Inverse2D</c>'s row/column
    /// passes) against the pre-existing scalar reference (<see cref="Av1InverseTransform.InverseDct"/>/
    /// <c>InverseAdst</c>/<c>InverseIdentity</c>) it's meant to be a faithful parallelization of: 4
    /// independently-random rows, packed one per SIMD lane, must produce bit-identical per-lane output to 4
    /// separate scalar calls on the same data, across every size the batched path is ever used at and a wide
    /// coefficient-magnitude range (including values large enough to approach the int32-truncation boundary
    /// the batched path's own <c>TruncateToInt32Batch</c>/<c>RoundBatch</c> exist to replicate). This is a
    /// stronger, more exhaustive check than <c>Inverse2D</c>'s own corpus/round-trip tests can offer on their
    /// own, since it isn't limited to whatever coefficient patterns real corpus content happens to produce.
    /// </summary>
    [Theory]
    [InlineData(2, 16)]
    [InlineData(3, 16)]
    [InlineData(4, 16)]
    [InlineData(5, 16)]
    [InlineData(6, 16)]
    [InlineData(2, 20)]
    [InlineData(6, 20)]
    public void InverseDctBatch_MatchesScalarReference_ForRandomRows(int n, int r)
    {
        var rng = new Random((n * 1000) + r);
        int len = 1 << n;

        for (int trial = 0; trial < 500; trial++)
        {
            var rows = new int[4][];
            var batch = new Vector256<long>[64];
            for (int lane = 0; lane < 4; lane++)
            {
                rows[lane] = new int[len];
                for (int idx = 0; idx < len; idx++)
                {
                    rows[lane][idx] = rng.Next(-(1 << 20), 1 << 20);
                }
            }

            for (int idx = 0; idx < len; idx++)
            {
                batch[idx] = Vector256.Create((long)rows[0][idx], (long)rows[1][idx], (long)rows[2][idx], (long)rows[3][idx]);
            }

            Av1InverseTransform.InverseDctBatch(batch, n, r);

            for (int lane = 0; lane < 4; lane++)
            {
                var expected = (int[])rows[lane].Clone();
                Av1InverseTransform.InverseDct(expected, n, r);

                for (int idx = 0; idx < len; idx++)
                {
                    Assert.Equal(expected[idx], (int)batch[idx].GetElement(lane));
                }
            }
        }
    }

    [Theory]
    [InlineData(2, 16)]
    [InlineData(3, 16)]
    [InlineData(4, 16)]
    [InlineData(2, 20)]
    [InlineData(4, 20)]
    public void InverseAdstBatch_MatchesScalarReference_ForRandomRows(int n, int r)
    {
        var rng = new Random((n * 2000) + r);
        int len = 1 << n;

        for (int trial = 0; trial < 500; trial++)
        {
            var rows = new int[4][];
            var batch = new Vector256<long>[64];
            for (int lane = 0; lane < 4; lane++)
            {
                rows[lane] = new int[len];
                for (int idx = 0; idx < len; idx++)
                {
                    rows[lane][idx] = rng.Next(-(1 << 20), 1 << 20);
                }
            }

            for (int idx = 0; idx < len; idx++)
            {
                batch[idx] = Vector256.Create((long)rows[0][idx], (long)rows[1][idx], (long)rows[2][idx], (long)rows[3][idx]);
            }

            Av1InverseTransform.InverseAdstBatch(batch, n, r);

            for (int lane = 0; lane < 4; lane++)
            {
                var expected = (int[])rows[lane].Clone();
                Av1InverseTransform.InverseAdst(expected, n, r);

                for (int idx = 0; idx < len; idx++)
                {
                    Assert.Equal(expected[idx], (int)batch[idx].GetElement(lane));
                }
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void InverseIdentityBatch_MatchesScalarReference_ForRandomRows(int n)
    {
        var rng = new Random(n * 3000);
        int len = 1 << n;

        for (int trial = 0; trial < 500; trial++)
        {
            var rows = new int[4][];
            var batch = new Vector256<long>[64];
            for (int lane = 0; lane < 4; lane++)
            {
                rows[lane] = new int[len];
                for (int idx = 0; idx < len; idx++)
                {
                    rows[lane][idx] = rng.Next(-(1 << 20), 1 << 20);
                }
            }

            for (int idx = 0; idx < len; idx++)
            {
                batch[idx] = Vector256.Create((long)rows[0][idx], (long)rows[1][idx], (long)rows[2][idx], (long)rows[3][idx]);
            }

            Av1InverseTransform.InverseIdentityBatch(batch, n);

            for (int lane = 0; lane < 4; lane++)
            {
                var expected = (int[])rows[lane].Clone();
                Av1InverseTransform.InverseIdentity(expected, n);

                for (int idx = 0; idx < len; idx++)
                {
                    Assert.Equal(expected[idx], (int)batch[idx].GetElement(lane));
                }
            }
        }
    }
}
