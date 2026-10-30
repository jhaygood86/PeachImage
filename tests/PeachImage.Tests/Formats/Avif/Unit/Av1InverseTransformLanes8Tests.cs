using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// The eight-lane int32 inverse transform (<see cref="Av1InverseTransform.Inverse2D"/> on AVX2 hardware) must
/// agree with the exact 64-bit path (<see cref="Av1InverseTransform.Inverse2DExact"/>) for every conformant
/// input: all transform sizes and types, 8- and 10-bit, dense and sparse coefficient extents, and the
/// lossless Walsh-Hadamard transform. Coefficient magnitudes stay inside the range a real dequantizer can
/// produce for the bit depth.
/// </summary>
public class Av1InverseTransformLanes8Tests
{
    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void Lanes8_MatchesExactPath(int bitDepth)
    {
        var random = new Random(2024 + bitDepth);
        int magnitude = bitDepth == 8 ? 20000 : 60000;

        for (int txSz = 0; txSz < Av1CoeffTables.AdjustedTxSize.Length; txSz++)
        {
            int w = Av1TxDimensions.Width[txSz];
            int h = Av1TxDimensions.Height[txSz];
            int tw = Math.Min(32, w);
            int th = Math.Min(32, h);
            int[] types = Math.Max(w, h) == 64 ? [Av1TxType.DctDct]
                : Math.Max(w, h) == 32 ? [Av1TxType.DctDct, Av1TxType.Idtx]
                : Enumerable.Range(0, Av1TxType.Count).ToArray();

            foreach (int txType in types)
            {
                for (int trial = 0; trial < 30; trial++)
                {
                    int nzRows = trial % 3 == 0 ? th : random.Next(1, th + 1);
                    int nzCols = trial % 3 == 0 ? tw : random.Next(1, tw + 1);
                    int range = trial % 4 == 0 ? magnitude : magnitude / 40;

                    var input = new int[64 * 64];
                    for (int i = 0; i < nzRows; i++)
                    {
                        for (int j = 0; j < nzCols; j++)
                        {
                            input[(i * 64) + j] = random.Next(3) == 0 ? 0 : random.Next(-range, range + 1);
                        }
                    }

                    var expected = new int[64 * 64];
                    var actual = new int[64 * 64];
                    Array.Fill(actual, 0x1234567);
                    Av1InverseTransform.Inverse2DExact(input, expected, txSz, txType, lossless: false, bitDepth, nzRows, nzCols);
                    Av1InverseTransform.Inverse2D(input, actual, txSz, txType, lossless: false, bitDepth, nzRows, nzCols);

                    Assert.True(
                        expected.AsSpan(0, w * h).SequenceEqual(actual.AsSpan(0, w * h)),
                        $"txSz={txSz} type={txType} bitDepth={bitDepth} nz={nzRows}x{nzCols} trial={trial}");
                }
            }
        }
    }

    [Fact]
    public void Lanes8_MatchesExactPath_ForLosslessWht()
    {
        var random = new Random(77);
        for (int trial = 0; trial < 200; trial++)
        {
            var input = new int[64 * 64];
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    input[(i * 64) + j] = random.Next(-4000, 4001);
                }
            }

            var expected = new int[64 * 64];
            var actual = new int[64 * 64];
            Av1InverseTransform.Inverse2DExact(input, expected, Av1TxSize.Tx4x4, Av1TxType.DctDct, lossless: true, 8);
            Av1InverseTransform.Inverse2D(input, actual, Av1TxSize.Tx4x4, Av1TxType.DctDct, lossless: true, 8);
            Assert.True(expected.AsSpan(0, 16).SequenceEqual(actual.AsSpan(0, 16)), $"trial={trial}");
        }
    }
}
