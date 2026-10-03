using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Decoding.Vp8.Dct;
using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>The vector forward DCT and the vector inverse DCT used by the encoder are held bit for bit to their scalar forms.</summary>
public class Vp8ForwardDctVectorTests
{
    private const int Stride = 24;

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    public void VectorForwardDct_MatchesScalar(int seed, int pattern)
    {
        Assert.SkipUnless(Vp8Interleave.IsSupported, "No two-vector interleave is available here.");

        var rng = new Random(seed);
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            var source = new byte[Stride * 8];
            var prediction = new byte[Stride * 8];
            for (int i = 0; i < source.Length; i++)
            {
                switch (pattern)
                {
                    case 0: // Random.
                        source[i] = (byte)rng.Next(256);
                        prediction[i] = (byte)rng.Next(256);
                        break;
                    case 1: // Worst-case swings.
                        source[i] = rng.Next(2) == 0 ? (byte)0 : (byte)255;
                        prediction[i] = rng.Next(2) == 0 ? (byte)0 : (byte)255;
                        break;
                    case 2: // Near-identical (small residuals, many exact zeros in b3).
                        source[i] = (byte)rng.Next(100, 110);
                        prediction[i] = (byte)(source[i] + rng.Next(-1, 2));
                        break;
                    default: // Identical: all-zero residual.
                        source[i] = prediction[i] = (byte)rng.Next(256);
                        break;
                }
            }

            int srcOffset = rng.Next(0, 4);
            int predOffset = rng.Next(0, 4);
            var expected = new short[16];
            var actual = new short[16];
            Vp8ForwardDct.TransformScalar(source, srcOffset, Stride, prediction, predOffset, Stride, expected);
            Vp8ForwardDct.TransformVector(source, srcOffset, Stride, prediction, predOffset, Stride, actual);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TransformAndAdd_MatchesScalarFullTransform(int seed)
    {
        var rng = new Random(seed);
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            var coefficients = new short[16];
            // A realistic mix: usually a few small coefficients, sometimes a large one, sometimes DC only.
            int count = rng.Next(0, 6);
            for (int k = 0; k < count; k++)
            {
                coefficients[rng.Next(16)] = (short)(rng.Next(8) == 0 ? rng.Next(-2000, 2000) : rng.Next(-40, 40));
            }

            var baseline = new byte[Stride * 4];
            rng.NextBytes(baseline);
            var expected = (byte[])baseline.Clone();
            var actual = (byte[])baseline.Clone();

            // Scalar reference: DC-only/zero handling is TransformAndAdd's own; use the same dispatch for
            // those and the plain scalar butterfly for the general case.
            bool anyAc = false;
            for (int i = 1; i < 16; i++)
            {
                anyAc |= coefficients[i] != 0;
            }

            if (anyAc)
            {
                Vp8ScalarInverseDct.TransformFullAndAdd(coefficients, expected, 3, Stride);
            }
            else if (coefficients[0] != 0)
            {
                Vp8ScalarInverseDct.TransformDcOnlyAndAdd(coefficients, expected, 3, Stride);
            }

            Vp8ScalarInverseDct.TransformAndAdd(coefficients, actual, 3, Stride);
            Assert.Equal(expected, actual);
        }
    }
}
