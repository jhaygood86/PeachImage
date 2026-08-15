using PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Pins the vectorized colour-conversion tier against the scalar one it has to reproduce exactly.
/// </summary>
public class Vp8ColorConverterTierTests
{
    /// <summary>
    /// Exhaustive over the entire 16,777,216-triple input domain, in rows of 256. This is small enough to be a
    /// proof rather than a sample, which matters because the two tiers stage their fixed-point arithmetic
    /// differently — the vector path has to truncate each product to 8 fractional bits individually, before
    /// summing, and descaling the sum instead would agree on most inputs while being wrong.
    /// </summary>
    [Fact]
    public void VectorTier_MatchesTheScalarTier_OverEveryPossibleSampleTriple()
    {
        var vector = new Vector128Vp8ColorConverter();
        var scalar = new Vp8ScalarColorConverter();

        const int Width = 256;
        byte[] y = new byte[Width];
        byte[] u = new byte[Width];
        byte[] v = new byte[Width];
        byte[] fromVector = new byte[Width * 3];
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

                vector.ConvertRow(y, u, v, fromVector, Width);
                scalar.ConvertRow(y, u, v, fromScalar, Width);

                if (!fromVector.AsSpan().SequenceEqual(fromScalar))
                {
                    // Only reached on failure; finds the first differing pixel so the message is actionable.
                    for (int x = 0; x < Width; x++)
                    {
                        Assert.Equal(fromScalar.AsSpan(x * 3, 3).ToArray(), fromVector.AsSpan(x * 3, 3).ToArray());
                    }
                }
            }
        }
    }

    /// <summary>
    /// The vector loop stops early to leave room for its 16-byte store and hands the rest to the scalar tail,
    /// so every row length has a different split between the two. Widths 0..40 cover all of them.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(33)]
    [InlineData(40)]
    public void VectorTier_MatchesTheScalarTier_AtEveryRowLength(int width)
    {
        var random = new Random(width + 1);
        byte[] y = new byte[Math.Max(width, 1)];
        byte[] u = new byte[Math.Max(width, 1)];
        byte[] v = new byte[Math.Max(width, 1)];
        random.NextBytes(y);
        random.NextBytes(u);
        random.NextBytes(v);

        byte[] fromVector = new byte[Math.Max(width * 3, 1)];
        byte[] fromScalar = new byte[Math.Max(width * 3, 1)];

        new Vector128Vp8ColorConverter().ConvertRow(y, u, v, fromVector, width);
        new Vp8ScalarColorConverter().ConvertRow(y, u, v, fromScalar, width);

        Assert.Equal(fromScalar, fromVector);
    }

    /// <summary>The vector store touches 16 bytes at a time; this checks it never writes past the row it was given.</summary>
    [Fact]
    public void VectorTier_DoesNotWritePastTheEndOfTheRow()
    {
        const int Width = 13;
        const byte Sentinel = 0xAB;

        byte[] y = new byte[Width];
        byte[] u = new byte[Width];
        byte[] v = new byte[Width];
        Array.Fill(y, (byte)200);
        Array.Fill(u, (byte)40);
        Array.Fill(v, (byte)210);

        byte[] destination = new byte[(Width * 3) + 8];
        Array.Fill(destination, Sentinel);

        new Vector128Vp8ColorConverter().ConvertRow(y, u, v, destination.AsSpan(0, Width * 3), Width);

        for (int i = Width * 3; i < destination.Length; i++)
        {
            Assert.Equal(Sentinel, destination[i]);
        }
    }
}
