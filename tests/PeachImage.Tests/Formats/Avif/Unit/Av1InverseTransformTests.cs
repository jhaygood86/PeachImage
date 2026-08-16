using System.Reflection;

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
}
