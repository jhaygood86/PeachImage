using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// The extent-aware <see cref="Av1InverseTransform.Inverse2D"/>
/// (zero-row skipping and the DC-only DCT_DCT shortcut) must be bit-identical to the full-extent form for any
/// block whose non-zero coefficients lie inside the declared extent, and must never read outside it.
/// </summary>
public class Av1InverseTransformSparseTests
{
    private static readonly int[] AllTxTypes = Enumerable.Range(0, Av1TxType.Count).ToArray();

    [Theory]
    [InlineData(8, false)]
    [InlineData(10, false)]
    [InlineData(8, true)]
    public void SparseExtent_MatchesFullExtent(int bitDepth, bool dcOnly)
    {
        var random = new Random(12345);

        for (int txSz = 0; txSz < Av1CoeffTables.AdjustedTxSize.Length; txSz++)
        {
            int w = Av1TxDimensions.Width[txSz];
            int h = Av1TxDimensions.Height[txSz];
            int tw = Math.Min(32, w);
            int th = Math.Min(32, h);
            int[] types = Math.Max(w, h) == 64 ? [Av1TxType.DctDct]
                : Math.Max(w, h) == 32 ? [Av1TxType.DctDct, Av1TxType.Idtx]
                : AllTxTypes;

            foreach (int txType in types)
            {
                for (int trial = 0; trial < 40; trial++)
                {
                    int nzRows = dcOnly ? 1 : random.Next(1, th + 1);
                    int nzCols = dcOnly ? 1 : random.Next(1, tw + 1);

                    var exact = new int[64 * 64];
                    var polluted = new int[64 * 64];
                    Array.Fill(polluted, 0x5A5A5A5);
                    int range = trial % 5 == 0 ? 20000 : 600;
                    for (int i = 0; i < nzRows; i++)
                    {
                        for (int j = 0; j < nzCols; j++)
                        {
                            int v = random.Next(3) == 0 && !dcOnly ? 0 : random.Next(-range, range + 1);
                            exact[(i * 64) + j] = v;
                            polluted[(i * 64) + j] = v;
                        }
                    }

                    var expected = new int[64 * 64];
                    var actual = new int[64 * 64];
                    Array.Fill(actual, 0x3C3C3C3);
                    Av1InverseTransform.Inverse2D(exact, expected, txSz, txType, lossless: false, bitDepth);
                    Av1InverseTransform.Inverse2D(polluted, actual, txSz, txType, lossless: false, bitDepth, nzRows, nzCols);

                    Assert.True(
                        expected.AsSpan(0, w * h).SequenceEqual(actual.AsSpan(0, w * h)),
                        $"txSz={txSz} type={txType} bitDepth={bitDepth} nz={nzRows}x{nzCols} trial={trial}");
                }
            }
        }
    }
}
