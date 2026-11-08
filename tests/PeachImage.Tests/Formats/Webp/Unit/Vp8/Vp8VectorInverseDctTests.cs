using PeachImage.Formats.Webp.Decoding.Vp8.Dct;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Pins <see cref="Vp8VectorInverseDct.TransformFullAndAdd"/> against
/// <see cref="Vp8ScalarInverseDct.TransformFullAndAdd"/>, which it must reproduce bit for bit.
/// </summary>
/// <remarks>
/// Every operation in the vectorized kernel is exact integer arithmetic — no floating-point rounding to
/// approximate, unlike JPEG's AAN kernel — so equivalence here is a provable, testable fact rather than a
/// "close enough" claim, and these tests hold it to exactly that standard: <c>Assert.Equal</c>, never a
/// tolerance.
/// </remarks>
public class Vp8VectorInverseDctTests
{
    private const int Stride = 11;

    [Fact]
    public void TransformFullAndAdd_MatchesTheScalarKernel_ForEveryBlockShape()
    {
        Assert.SkipUnless(Vp8VectorInverseDct.CanTransform, "No two-vector interleave (SSE2 or Arm64 AdvSimd) is available here.");

        foreach (var coefficients in EnumerateStructuredBlocks())
        {
            AssertAgrees(coefficients);
        }
    }

    /// <summary>
    /// VP8's quantized coefficients are bounded (RFC 6386's largest category tops out at 2114, doubled by the
    /// largest quantizer), but this sweeps well past that on both signs specifically to prove the kernel has
    /// no overflow-dependent shortcut — if it were subtly relying on the values staying small, wide random
    /// values would be what exposes it.
    /// </summary>
    [Fact]
    public void TransformFullAndAdd_MatchesTheScalarKernel_AcrossWideRandomCoefficients()
    {
        Assert.SkipUnless(Vp8VectorInverseDct.CanTransform, "No two-vector interleave (SSE2 or Arm64 AdvSimd) is available here.");

        var random = new Random(271828);

        for (int trial = 0; trial < 20_000; trial++)
        {
            short[] coefficients = new short[16];
            for (int i = 0; i < 16; i++)
            {
                coefficients[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
            }

            // At least one AC coefficient, matching the caller's contract (an all-AC-zero block routes to
            // the DC-only path instead, which this kernel doesn't implement).
            if (coefficients[1..].AsSpan().IndexOfAnyExcept((short)0) < 0)
            {
                coefficients[1] = 1;
            }

            AssertAgrees(coefficients);
        }
    }

    /// <summary>Every existing base pixel value, so the clamp at both ends of [0,255] is exercised regardless of what the random coefficient sweep happens to produce.</summary>
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)254)]
    [InlineData((byte)255)]
    public void TransformFullAndAdd_MatchesTheScalarKernel_AtEveryBasePixelExtreme(byte basePixel)
    {
        Assert.SkipUnless(Vp8VectorInverseDct.CanTransform, "No two-vector interleave (SSE2 or Arm64 AdvSimd) is available here.");

        var random = new Random(basePixel + 1);

        for (int trial = 0; trial < 500; trial++)
        {
            short[] coefficients = new short[16];
            for (int i = 0; i < 16; i++)
            {
                coefficients[i] = (short)random.Next(-2114, 2115);
            }

            coefficients[1] = coefficients[1] == 0 ? (short)1 : coefficients[1];

            AssertAgrees(coefficients, basePixel);
        }
    }

    /// <summary>
    /// The 16-bit kernel is only used when every coefficient is within +-2047, where its intermediates just fit. The largest
    /// intermediates come from blocks whose coefficients all sit at the limit with the signs that make the butterflies add up, so this
    /// tries every one of the 2^16 sign patterns at the limit (and, as a fallback check, one past it) against the scalar kernel.
    /// </summary>
    [Theory]
    [InlineData(2047)]
    [InlineData(2048)]
    public void TransformFullAndAdd_MatchesTheScalarKernel_ForEverySignPatternAtTheSixteenBitLimit(int magnitude)
    {
        Assert.SkipUnless(Vp8VectorInverseDct.CanTransform, "No two-vector interleave (SSE2 or Arm64 AdvSimd) is available here.");

        short[] coefficients = new short[16];
        for (int pattern = 0; pattern < 1 << 16; pattern++)
        {
            for (int i = 0; i < 16; i++)
            {
                coefficients[i] = (short)(((pattern >> i) & 1) != 0 ? -magnitude : magnitude);
            }

            AssertAgrees(coefficients, (byte)(pattern & 0x80));
        }
    }

    /// <summary>
    /// Blocks whose only nonzero coefficients are raster positions 0, 1 and 4 take a shortcut; it must give the full transform's result
    /// for any values there, including the extremes of the 16-bit input range and zeros in either AC position.
    /// </summary>
    [Fact]
    public void AddSparseAc_MatchesTheScalarKernel_ForAnyCoefficientsAtPositionsZeroOneAndFour()
    {
        var random = new Random(161803);
        short[] extremes = [0, 1, -1, 2, -2, 255, -255, 2047, -2047, 2114, -2114, 32767, -32768, 12345, -12345];

        for (int trial = 0; trial < 30_000; trial++)
        {
            short[] coefficients = new short[16];
            if (trial < extremes.Length * extremes.Length * extremes.Length)
            {
                coefficients[0] = extremes[trial % extremes.Length];
                coefficients[1] = extremes[trial / extremes.Length % extremes.Length];
                coefficients[4] = extremes[trial / (extremes.Length * extremes.Length)];
            }
            else
            {
                coefficients[0] = (short)random.Next(short.MinValue, short.MaxValue + 1);
                coefficients[1] = (short)random.Next(-4000, 4001);
                coefficients[4] = (short)random.Next(-4000, 4001);
            }

            byte basePixel = (byte)(trial % 3 == 0 ? 0 : trial % 3 == 1 ? 128 : 255);
            byte[] expected = MakePlane(basePixel);
            byte[] actual = MakePlane(basePixel);

            Vp8ScalarInverseDct.TransformFullAndAdd(coefficients, expected, Stride + 1, Stride);
            Vp8VectorInverseDct.AddSparseAc(ref coefficients[0], ref actual[Stride + 1], Stride);

            Assert.Equal(expected, actual);
        }
    }

    private static IEnumerable<short[]> EnumerateStructuredBlocks()
    {
        // One AC coefficient at each position, at both signs and at a magnitude that will saturate the clamp.
        for (int position = 1; position < 16; position++)
        {
            foreach (short magnitude in new short[] { 1, -1, 2114, -2114 })
            {
                short[] block = new short[16];
                block[position] = magnitude;
                yield return block;
            }
        }

        // DC plus every AC position nonzero together, plus the all-extremes case.
        short[] allSet = new short[16];
        for (int i = 0; i < 16; i++)
        {
            allSet[i] = (short)(i % 2 == 0 ? 500 : -500);
        }

        yield return allSet;

        short[] dcAndOneAc = new short[16];
        dcAndOneAc[0] = 1000;
        dcAndOneAc[5] = -1000;
        yield return dcAndOneAc;
    }

    private static void AssertAgrees(short[] coefficients, byte basePixel = 128)
    {
        byte[] planeExpected = MakePlane(basePixel);
        byte[] planeActual = MakePlane(basePixel);

        Vp8ScalarInverseDct.TransformFullAndAdd(coefficients, planeExpected, Stride + 1, Stride);
        Vp8VectorInverseDct.TransformFullAndAdd(coefficients, planeActual, Stride + 1, Stride);

        Assert.Equal(planeExpected, planeActual);
    }

    /// <summary>A plane with a non-uniform surrounding pattern, so a transform that writes to the wrong offset shows up rather than blending into a flat background.</summary>
    private static byte[] MakePlane(byte basePixel)
    {
        byte[] plane = new byte[Stride * Stride];
        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = (byte)((i * 7 % 251) ^ basePixel);
        }

        // The 4x4 block under test starts flat at basePixel so ClampAdd's behaviour is driven purely by the
        // coefficients under test, not by incidental variation baked into the surrounding pattern.
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                plane[((Stride + 1) + (y * Stride)) + x] = basePixel;
            }
        }

        return plane;
    }
}
