using PeachImage.Formats.Webp.Decoding.Vp8.Dct;
using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8Encoding;

/// <summary>
/// Validates <see cref="Vp8ForwardDct"/> by round-tripping through the real, unmodified
/// <see cref="Vp8ScalarInverseDct"/>: forward-transforming a residual and then inverse-transforming it back onto
/// the same prediction must reproduce the original source pixels within a small, bounded error (the forward and
/// inverse transforms are a calibrated pair, not exact algebraic inverses of each other -- see
/// <see cref="Vp8ForwardTransformConstants"/>'s remarks -- so exact equality is not the right assertion; bounded
/// per-pixel error is).
/// </summary>
public class Vp8ForwardDctTests
{
    private const int Stride = 8;

    public static IEnumerable<object[]> RepresentativeBlocks()
    {
        yield return [Flat(0), Flat(128), "flat identical"];
        yield return [Flat(200), Flat(50), "flat large offset"];
        yield return [Gradient(), Flat(128), "gradient vs flat prediction"];
        yield return [Checkerboard(0, 255), Flat(128), "checkerboard vs flat prediction"];
        yield return [Random(1), Random(2), "random vs random"];
        yield return [Random(3), Flat(128), "random vs flat prediction"];
    }

    [Theory]
    [MemberData(nameof(RepresentativeBlocks))]
    public void Transform_RoundTripsThroughRealInverseTransform_WithinBoundedError(byte[] source, byte[] prediction, string label)
    {
        Span<short> coefficients = stackalloc short[16];
        Vp8ForwardDct.Transform(source, 0, Stride, prediction, 0, Stride, coefficients);

        byte[] reconstructed = (byte[])prediction.Clone();
        Vp8ScalarInverseDct.TransformFullAndAdd(coefficients, reconstructed, 0, Stride);

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int idx = (y * Stride) + x;
                int expected = source[idx];
                int actual = reconstructed[idx];
                Assert.True(Math.Abs(expected - actual) <= 2, $"{label} at ({x},{y}): expected {expected}, got {actual}.");
            }
        }
    }

    /// <summary>
    /// A truly zero residual does not forward-transform to exactly-zero coefficients -- libwebp's real
    /// <c>FTransform_C</c> (transcribed verbatim here) has rounding biases (1812, 937) that leave small nonzero
    /// pre-quantization values even for a flat block; real encoders rely on quantization rounding these away,
    /// not on the raw transform being exactly zero-preserving. This asserts the bound those biases guarantee
    /// instead.
    /// </summary>
    [Fact]
    public void Transform_ZeroResidual_ProducesOnlySmallResidualCoefficients()
    {
        byte[] block = Flat(100);
        Span<short> coefficients = stackalloc short[16];

        Vp8ForwardDct.Transform(block, 0, Stride, block, 0, Stride, coefficients);

        foreach (short c in coefficients)
        {
            Assert.True(Math.Abs((int)c) <= 4, $"Expected a near-zero coefficient for a flat block, got {c}.");
        }
    }

    private static byte[] Flat(byte value)
    {
        var block = new byte[Stride * 4];
        Array.Fill(block, value);
        return block;
    }

    private static byte[] Gradient()
    {
        var block = new byte[Stride * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                block[(y * Stride) + x] = (byte)((y * 16) + (x * 16));
            }
        }

        return block;
    }

    private static byte[] Checkerboard(byte a, byte b)
    {
        var block = new byte[Stride * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                block[(y * Stride) + x] = ((x + y) % 2 == 0) ? a : b;
            }
        }

        return block;
    }

    private static byte[] Random(int seed)
    {
        var random = new Random(seed);
        var block = new byte[Stride * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                block[(y * Stride) + x] = (byte)random.Next(256);
            }
        }

        return block;
    }
}
