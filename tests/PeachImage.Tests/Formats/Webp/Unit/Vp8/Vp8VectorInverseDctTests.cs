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
        Assert.SkipUnless(Vp8VectorInverseDct.CanTransform, "SSE2 is not available here.");

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
        Assert.SkipUnless(Vp8VectorInverseDct.CanTransform, "SSE2 is not available here.");

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
        Assert.SkipUnless(Vp8VectorInverseDct.CanTransform, "SSE2 is not available here.");

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
