using PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Pins the 16-bit-lane SSE2 colour-conversion tier against the scalar tier it has to reproduce exactly. Every
/// test is a no-op on hardware without SSSE3 (the tier is never selected there).
/// </summary>
public class Sse2Vp8ColorConverterTests
{
    /// <summary>Exhaustive over all 16,777,216 (y, u, v) triples: a proof, not a sample, for the saturating-arithmetic formulation.</summary>
    [Fact]
    public void MatchesTheScalarTier_OverEveryPossibleSampleTriple()
    {
        if (!Sse2Vp8ColorConverter.IsSupported)
        {
            return;
        }

        var sse2 = new Sse2Vp8ColorConverter();
        var scalar = new Vp8ScalarColorConverter();

        const int Width = 256;
        byte[] y = new byte[Width];
        byte[] u = new byte[Width];
        byte[] v = new byte[Width];
        byte[] fromSse2 = new byte[Width * 3];
        byte[] fromScalar = new byte[Width * 3];

        for (int i = 0; i < Width; i++)
        {
            y[i] = (byte)i;
        }

        for (int uValue = 0; uValue <= 255; uValue++)
        {
            Array.Fill(u, (byte)uValue);

            for (int vValue = 0; vValue <= 255; vValue++)
            {
                Array.Fill(v, (byte)vValue);

                sse2.ConvertRow(y, u, v, fromSse2, Width);
                scalar.ConvertRow(y, u, v, fromScalar, Width);

                if (!fromSse2.AsSpan().SequenceEqual(fromScalar))
                {
                    for (int x = 0; x < Width; x++)
                    {
                        Assert.Equal(fromScalar.AsSpan(x * 3, 3).ToArray(), fromSse2.AsSpan(x * 3, 3).ToArray());
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(95)]
    [InlineData(96)]
    [InlineData(97)]
    [InlineData(100)]
    public void MatchesTheScalarTier_AtEveryRowLength(int width)
    {
        if (!Sse2Vp8ColorConverter.IsSupported)
        {
            return;
        }

        var random = new Random(width + 7);
        byte[] y = new byte[Math.Max(width, 1)];
        byte[] u = new byte[Math.Max(width, 1)];
        byte[] v = new byte[Math.Max(width, 1)];
        random.NextBytes(y);
        random.NextBytes(u);
        random.NextBytes(v);

        byte[] fromSse2 = new byte[Math.Max(width * 3, 1)];
        byte[] fromScalar = new byte[Math.Max(width * 3, 1)];

        new Sse2Vp8ColorConverter().ConvertRow(y, u, v, fromSse2, width);
        new Vp8ScalarColorConverter().ConvertRow(y, u, v, fromScalar, width);

        Assert.Equal(fromScalar, fromSse2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(47)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(97)]
    public void WithAlpha_MatchesScalarConversionPlusTheAlphaPlane(int width)
    {
        if (!Sse2Vp8ColorConverter.IsSupported)
        {
            return;
        }

        var random = new Random(width + 99);
        byte[] y = new byte[width];
        byte[] u = new byte[width];
        byte[] v = new byte[width];
        byte[] alpha = new byte[width];
        random.NextBytes(y);
        random.NextBytes(u);
        random.NextBytes(v);
        random.NextBytes(alpha);

        byte[] rgb = new byte[width * 3];
        new Vp8ScalarColorConverter().ConvertRow(y, u, v, rgb, width);

        byte[] expected = new byte[width * 4];
        for (int x = 0; x < width; x++)
        {
            expected[(x * 4) + 0] = rgb[(x * 3) + 0];
            expected[(x * 4) + 1] = rgb[(x * 3) + 1];
            expected[(x * 4) + 2] = rgb[(x * 3) + 2];
            expected[(x * 4) + 3] = alpha[x];
        }

        byte[] actual = new byte[width * 4];
        new Sse2Vp8ColorConverter().ConvertRowWithAlpha(y, u, v, alpha, actual, width);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DoesNotWritePastTheEndOfTheRow()
    {
        if (!Sse2Vp8ColorConverter.IsSupported)
        {
            return;
        }

        const int Width = 37;
        const byte Sentinel = 0xAB;

        byte[] y = new byte[Width];
        byte[] u = new byte[Width];
        byte[] v = new byte[Width];
        Array.Fill(y, (byte)200);
        Array.Fill(u, (byte)40);
        Array.Fill(v, (byte)210);

        byte[] destination = new byte[(Width * 3) + 16];
        Array.Fill(destination, Sentinel);

        new Sse2Vp8ColorConverter().ConvertRow(y, u, v, destination.AsSpan(0, Width * 3), Width);

        for (int i = Width * 3; i < destination.Length; i++)
        {
            Assert.Equal(Sentinel, destination[i]);
        }
    }
}
