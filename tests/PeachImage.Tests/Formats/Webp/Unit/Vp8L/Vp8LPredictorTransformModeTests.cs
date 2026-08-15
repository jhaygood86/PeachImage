using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

/// <summary>
/// Checks each per-mode specialization of the predictor inverse against the mode-dispatched reference form
/// (<see cref="Vp8LPredictorTransform.Predict(int, ReadOnlySpan{uint}, int, int)"/>) that the production path
/// no longer calls.
/// </summary>
/// <remarks>
/// The specializations carry the neighborhood forward in registers rather than re-reading it, which is exactly
/// the kind of rewrite that can silently disagree with the straightforward version at a run or row boundary —
/// most obviously at the last column, where VP8L deliberately lets the "top-right" read run one past the end
/// of the row above and land on the current row's own first pixel. Every mode 0..15 is covered, including the
/// two the spec assigns no meaning to, and tile widths are varied so runs start and end at many offsets.
/// </remarks>
public class Vp8LPredictorTransformModeTests
{
    [Theory]
    [InlineData(2)] // 4-pixel tiles: many short runs per row, so run boundaries land everywhere.
    [InlineData(3)]
    [InlineData(5)] // 32-pixel tiles: wider than the image, so each row is a single run.
    public void EveryMode_MatchesTheReferencePredictor(int bits)
    {
        const int Width = 23; // deliberately not a multiple of any tile width, nor of a vector width.
        const int Height = 11;

        for (int mode = 0; mode <= 15; mode++)
        {
            uint[] residuals = MakeResiduals(Width * Height, seed: 1000 + mode);
            var transform = MakeTransform(Width, Height, bits, mode);

            uint[] actual = (uint[])residuals.Clone();
            Vp8LPredictorTransform.ApplyInverse(actual, transform);

            uint[] expected = ReferenceApplyInverse(residuals, Width, Height, bits, mode);

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>Mixed modes across the tile grid, so runs switch predictor from tile to tile the way a real image does.</summary>
    [Fact]
    public void MixedModesAcrossTiles_MatchTheReferencePredictor()
    {
        const int Width = 40;
        const int Height = 17;
        const int Bits = 2;

        int tilesPerRow = (Width + (1 << Bits) - 1) >> Bits;
        int tileRows = (Height + (1 << Bits) - 1) >> Bits;

        var random = new Random(4242);
        uint[] tileData = new uint[tilesPerRow * tileRows];
        for (int i = 0; i < tileData.Length; i++)
        {
            tileData[i] = (uint)random.Next(0, 16) << 8;
        }

        var transform = new Vp8LTransform
        {
            Type = Vp8LTransformType.Predictor,
            Bits = Bits,
            Xsize = Width,
            Ysize = Height,
            Data = tileData,
        };

        uint[] residuals = MakeResiduals(Width * Height, seed: 7);

        uint[] actual = (uint[])residuals.Clone();
        Vp8LPredictorTransform.ApplyInverse(actual, transform);

        uint[] expected = ReferenceApplyInverse(residuals, Width, Height, Bits, tileData, tilesPerRow);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Biases heavily toward 0x00 and 0xFF per channel. The channel-clamping modes (12, 13) and the
    /// sum-of-absolute-differences comparison in mode 11 only take their interesting branches at the extremes,
    /// which uniform random bytes almost never reach.
    /// </summary>
    private static uint[] MakeResiduals(int count, int seed)
    {
        var random = new Random(seed);
        uint[] pixels = new uint[count];

        for (int i = 0; i < count; i++)
        {
            uint value = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                uint b = random.Next(4) switch
                {
                    0 => 0x00u,
                    1 => 0xFFu,
                    2 => (uint)random.Next(0xFC, 0x100),
                    _ => (uint)random.Next(0x100),
                };

                value |= b << (channel * 8);
            }

            pixels[i] = value;
        }

        return pixels;
    }

    private static Vp8LTransform MakeTransform(int width, int height, int bits, int mode)
    {
        int tilesPerRow = (width + (1 << bits) - 1) >> bits;
        int tileRows = (height + (1 << bits) - 1) >> bits;
        uint[] tileData = new uint[tilesPerRow * tileRows];
        Array.Fill(tileData, (uint)mode << 8);

        return new Vp8LTransform
        {
            Type = Vp8LTransformType.Predictor,
            Bits = bits,
            Xsize = width,
            Ysize = height,
            Data = tileData,
        };
    }

    private static uint[] ReferenceApplyInverse(uint[] residuals, int width, int height, int bits, int mode)
    {
        int tilesPerRow = (width + (1 << bits) - 1) >> bits;
        int tileRows = (height + (1 << bits) - 1) >> bits;
        uint[] tileData = new uint[tilesPerRow * tileRows];
        Array.Fill(tileData, (uint)mode << 8);

        return ReferenceApplyInverse(residuals, width, height, bits, tileData, tilesPerRow);
    }

    /// <summary>
    /// A direct transliteration of the pre-specialization inverse: read the mode per pixel, call the shared
    /// mode-dispatched predictor, add. Intentionally the slow, obvious shape — it is the oracle, not the
    /// implementation.
    /// </summary>
    private static uint[] ReferenceApplyInverse(uint[] residuals, int width, int height, int bits, uint[] tileData, int tilesPerRow)
    {
        uint[] pixels = (uint[])residuals.Clone();

        pixels[0] = AddWrapping(pixels[0], 0xFF000000u);
        for (int x = 1; x < width; x++)
        {
            pixels[x] = AddWrapping(pixels[x], pixels[x - 1]);
        }

        for (int y = 1; y < height; y++)
        {
            int rowBase = y * width;
            pixels[rowBase] = AddWrapping(pixels[rowBase], pixels[rowBase - width]);

            for (int x = 1; x < width; x++)
            {
                int index = rowBase + x;
                int mode = (int)((tileData[((y >> bits) * tilesPerRow) + (x >> bits)] >> 8) & 0xF);
                pixels[index] = AddWrapping(pixels[index], Vp8LPredictorTransform.Predict(mode, pixels, index, width));
            }
        }

        return pixels;
    }

    private static uint AddWrapping(uint a, uint b) => Vp8LPixelMath.AddWrapping(a, b);
}

/// <summary>
/// Pins the vectorized <c>Select</c> (predictor mode 11) against the scalar form it replaced. The mode tests
/// above cannot do this on their own: their oracle routes through the same <c>Select</c>, so both sides would
/// move together if it were wrong.
/// </summary>
public class Vp8LSelectPredictorTests
{
    /// <summary>
    /// Exhaustive over one channel with the other three held at revealing values, plus a full sweep of the
    /// single-channel case. Mode 11's decision is a comparison of two sums, so it only changes behaviour where
    /// those sums are close — random sampling mostly lands far from the boundary.
    /// </summary>
    [Fact]
    public void Select_MatchesTheScalarReference_AcrossASingleChannelSweep()
    {
        for (int av = 0; av <= 255; av++)
        {
            for (int bv = 0; bv <= 255; bv++)
            {
                for (int cv = 0; cv <= 255; cv += 17)
                {
                    AssertSelectAgrees((uint)av, (uint)bv, (uint)cv);
                }
            }
        }
    }

    [Fact]
    public void Select_MatchesTheScalarReference_AcrossRandomFullPixels()
    {
        var random = new Random(20260815);

        for (int i = 0; i < 300_000; i++)
        {
            AssertSelectAgrees(NextPixel(random), NextPixel(random), NextPixel(random));
        }
    }

    /// <summary>Every channel of every operand at an extreme, where the two distance sums are most likely to tie.</summary>
    [Fact]
    public void Select_MatchesTheScalarReference_AtChannelExtremes()
    {
        byte[] values = [0, 1, 2, 127, 128, 129, 254, 255];

        foreach (byte a in values)
        {
            foreach (byte b in values)
            {
                foreach (byte c in values)
                {
                    AssertSelectAgrees(Broadcast(a), Broadcast(b), Broadcast(c));
                    AssertSelectAgrees(Broadcast(a), Broadcast(b), (uint)c << 24);
                    AssertSelectAgrees((uint)a << 24 | b, Broadcast(b), Broadcast(c));
                }
            }
        }
    }

    /// <summary>
    /// Reaches <c>Select</c> through the reference predictor, which is the only route to it from outside the
    /// type. Mode 11 calls <c>Select(top, left, topLeft)</c>, so with <c>width: 2</c> and <c>index: 3</c> the
    /// buffer below places <c>c</c> at top-left (index 0), <c>a</c> at top (index 1) and <c>b</c> at left
    /// (index 2), making the call exactly <c>Select(a, b, c)</c>.
    /// </summary>
    private static void AssertSelectAgrees(uint a, uint b, uint c)
    {
        uint expected = Vp8LPredictorTransform.SelectScalar(a, b, c);
        uint actual = Vp8LPredictorTransform.Predict(11, [c, a, b, 0u], index: 3, width: 2);

        Assert.Equal(expected, actual);
    }

    private static uint Broadcast(byte value) => value * 0x01010101u;

    private static uint NextPixel(Random random)
    {
        // Weighted toward extremes for the same reason MakeResiduals is.
        uint value = 0;
        for (int channel = 0; channel < 4; channel++)
        {
            uint b = random.Next(3) switch
            {
                0 => 0x00u,
                1 => 0xFFu,
                _ => (uint)random.Next(0x100),
            };

            value |= b << (channel * 8);
        }

        return value;
    }
}
