using System.Numerics;
using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.VarDct;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.Features;

/// <summary>The noise-level lookup table of a frame: eight 10-bit strengths indexed by local intensity.</summary>
internal sealed class JxlNoiseParams
{
    public const int PointCount = 8;
    private const float Precision = 1024f;

    public float[] Lut { get; } = new float[PointCount];

    public bool HasAny
    {
        get
        {
            foreach (float v in Lut)
            {
                if (Math.Abs(v) > 1e-3f)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static JxlNoiseParams Read(ref JxlBitReader br)
    {
        var result = new JxlNoiseParams();
        for (int i = 0; i < PointCount; i++)
        {
            result.Lut[i] = br.ReadBits(10) / Precision;
        }

        br.ThrowIfOverrun();
        return result;
    }
}

/// <summary>
/// Synthesises the frame's noise: a deterministic pseudo-random field per group, high-pass filtered, scaled by a
/// brightness-dependent strength and added to the XYB planes (correlated between the red and green primaries).
/// </summary>
internal static class JxlNoise
{
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    /// <summary>Adds noise to the three XYB <paramref name="planes"/> in place.</summary>
    public static void Add(
        JxlNoiseParams parameters,
        float[][] planes,
        int stride,
        int width,
        int height,
        int groupDim,
        float yToX,
        float yToB,
        uint visibleFrameIndex,
        uint nonVisibleFrameIndex,
        bool vectorized = true)
    {
        if (!parameters.HasAny)
        {
            return;
        }

        var pool = System.Buffers.ArrayPool<float>.Shared;
        var random = new float[3][];
        var convolved = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            random[c] = pool.Rent(width * height);
            convolved[c] = pool.Rent(width * height);
        }

        try
        {
            GenerateRandomFields(random, width, height, groupDim, visibleFrameIndex, nonVisibleFrameIndex);
            for (int c = 0; c < 3; c++)
            {
                Convolve(random[c], convolved[c], width, height, vectorized);
            }

            float[] lut = parameters.Lut;
            RowParallel.For(height, y => AddRow(planes, stride, y, width, convolved, lut, yToX, yToB, vectorized));
        }
        finally
        {
            for (int c = 0; c < 3; c++)
            {
                pool.Return(random[c]);
                pool.Return(convolved[c]);
            }
        }
    }

    private const float RgCorrelated = 0.9921875f; // 127/128
    private const float RgIndependent = 0.0078125f; // 1/128
    private const float Normalization = 0.22f;

    private static void AddRow(float[][] planes, int stride, int y, int width, float[][] convolved, float[] lut, float yToX, float yToB, bool vectorized)
    {
        int row = y * stride;
        int noiseRow = y * width;
        int x = 0;
        if (vectorized && Vector.IsHardwareAccelerated)
        {
            var vYToX = new Vector<float>(yToX);
            var vYToB = new Vector<float>(yToB);
            var half = new Vector<float>(0.5f);
            var norm = new Vector<float>(Normalization);
            var independent = new Vector<float>(RgIndependent);
            var correlated = new Vector<float>(RgCorrelated);
            for (; x <= width - Vector<float>.Count; x += Vector<float>.Count)
            {
                var vx = new Vector<float>(planes[0], row + x);
                var vy = new Vector<float>(planes[1], row + x);
                var vb = new Vector<float>(planes[2], row + x);
                var strengthG = Strength(lut, (vy - vx) * half);
                var strengthR = Strength(lut, (vy + vx) * half);
                var rndR = new Vector<float>(convolved[0], noiseRow + x) * norm;
                var rndG = new Vector<float>(convolved[1], noiseRow + x) * norm;
                var rndC = new Vector<float>(convolved[2], noiseRow + x) * norm;
                var redNoise = strengthR * ((independent * rndR) + (correlated * rndC));
                var greenNoise = strengthG * ((independent * rndG) + (correlated * rndC));
                var rg = redNoise + greenNoise;
                (((vYToX * rg) + (redNoise - greenNoise)) + vx).CopyTo(planes[0], row + x);
                (vy + rg).CopyTo(planes[1], row + x);
                ((vYToB * rg) + vb).CopyTo(planes[2], row + x);
            }
        }

        for (; x < width; x++)
        {
            float vx = planes[0][row + x];
            float vy = planes[1][row + x];
            float vb = planes[2][row + x];
            float strengthG = Strength(lut, (vy - vx) * 0.5f);
            float strengthR = Strength(lut, (vy + vx) * 0.5f);
            float rndR = convolved[0][noiseRow + x] * Normalization;
            float rndG = convolved[1][noiseRow + x] * Normalization;
            float rndC = convolved[2][noiseRow + x] * Normalization;
            float redNoise = strengthR * ((RgIndependent * rndR) + (RgCorrelated * rndC));
            float greenNoise = strengthG * ((RgIndependent * rndG) + (RgCorrelated * rndC));
            float rg = redNoise + greenNoise;
            planes[0][row + x] = ((yToX * rg) + (redNoise - greenNoise)) + vx;
            planes[1][row + x] = vy + rg;
            planes[2][row + x] = (yToB * rg) + vb;
        }
    }

    // Interpolates the noise table at x (a luminance-like value around [0, 1]) and clamps the result to [0, 1].
    private static float Strength(float[] lut, float x)
    {
        const int scale = JxlNoiseParams.PointCount - 2;
        float scaled = Math.Max(0f, x * scale);
        float floor = MathF.Floor(scaled);
        float frac = scaled - floor;
        if (scaled >= scale + 1)
        {
            floor = scale;
            frac = 1f;
        }

        int index = (int)floor;
        float low = lut[index];
        float high = lut[index + 1];
        float value = ((high - low) * frac) + low;
        return Math.Max(0f, Math.Min(value, 1f));
    }

    private static Vector<float> Strength(float[] lut, Vector<float> x)
    {
        const int scale = JxlNoiseParams.PointCount - 2;
        var scaled = Vector.Max(Vector<float>.Zero, x * new Vector<float>(scale));
        var floor = Vector.Floor(scaled);
        var frac = scaled - floor;
        var big = Vector.GreaterThanOrEqual(scaled, new Vector<float>(scale + 1));
        floor = Vector.ConditionalSelect(big, new Vector<float>(scale), floor);
        frac = Vector.ConditionalSelect(big, Vector<float>.One, frac);

        // Table lookup by selecting among the (few) entries.
        var low = new Vector<float>(lut[0]);
        var high = new Vector<float>(lut[1]);
        for (int i = 1; i <= scale; i++)
        {
            var atLeast = Vector.GreaterThanOrEqual(floor, new Vector<float>(i));
            low = Vector.ConditionalSelect(atLeast, new Vector<float>(lut[i]), low);
            high = Vector.ConditionalSelect(atLeast, new Vector<float>(lut[i + 1]), high);
        }

        var value = ((high - low) * frac) + low;
        return Vector.Max(Vector<float>.Zero, Vector.Min(value, Vector<float>.One));
    }

    // 5x5 high-pass: 4 * (1 - box kernel), with symmetric extension at the frame edges. The 24 neighbours are summed in the
    // order of the reference decoder: the four outer rows column by column, then the other four pixels of the centre row.
    private static void Convolve(float[] source, float[] output, int width, int height, bool vectorized)
    {
        var xs = new int[width + 4];
        for (int i = 0; i < xs.Length; i++)
        {
            xs[i] = FramePostProcessing.Mirror(i - 2, width);
        }

        RowParallel.For(height, y =>
        {
            int r0 = FramePostProcessing.Mirror(y - 2, height) * width;
            int r1 = FramePostProcessing.Mirror(y - 1, height) * width;
            int r2 = y * width;
            int r3 = FramePostProcessing.Mirror(y + 1, height) * width;
            int r4 = FramePostProcessing.Mirror(y + 2, height) * width;
            int x = 0;
            if (vectorized && Vector.IsHardwareAccelerated && width >= 4 + Vector<float>.Count)
            {
                var k1 = new Vector<float>(0.16f);
                var k2 = new Vector<float>(-3.84f);
                for (; x < 2; x++)
                {
                    output[r2 + x] = ConvolvePixel(source, xs, x, r0, r1, r2, r3, r4);
                }

                for (; x + Vector<float>.Count <= width - 2; x += Vector<float>.Count)
                {
                    var others = Vector<float>.Zero;
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        others += new Vector<float>(source, r0 + x + dx);
                        others += new Vector<float>(source, r1 + x + dx);
                        others += new Vector<float>(source, r3 + x + dx);
                        others += new Vector<float>(source, r4 + x + dx);
                    }

                    others += new Vector<float>(source, r2 + x - 2);
                    others += new Vector<float>(source, r2 + x - 1);
                    others += new Vector<float>(source, r2 + x + 1);
                    others += new Vector<float>(source, r2 + x + 2);
                    ((others * k1) + (new Vector<float>(source, r2 + x) * k2)).CopyTo(output, r2 + x);
                }
            }

            for (; x < width; x++)
            {
                output[r2 + x] = ConvolvePixel(source, xs, x, r0, r1, r2, r3, r4);
            }
        });
    }

    private static float ConvolvePixel(float[] source, int[] xs, int x, int r0, int r1, int r2, int r3, int r4)
    {
        float others = 0;
        for (int dx = -2; dx <= 2; dx++)
        {
            int xi = xs[x + dx + 2];
            others += source[r0 + xi];
            others += source[r1 + xi];
            others += source[r3 + xi];
            others += source[r4 + xi];
        }

        others += source[r2 + xs[x]];
        others += source[r2 + xs[x + 1]];
        others += source[r2 + xs[x + 3]];
        others += source[r2 + xs[x + 4]];
        return (others * 0.16f) + (source[r2 + x] * -3.84f);
    }

    private static void GenerateRandomFields(float[][] planes, int width, int height, int groupDim, uint visible, uint nonVisible)
    {
        int groupsX = (width + groupDim - 1) / groupDim;
        int groupsY = (height + groupDim - 1) / groupDim;

        // Each group seeds its own generator and fills its own rectangle, so groups can be generated concurrently.
        JxlParallel.For(groupsX * groupsY, group =>
        {
            int gx = group % groupsX;
            int gy = group / groupsX;
            int x0 = gx * groupDim;
            int y0 = gy * groupDim;
            int w = Math.Min(groupDim, width - x0);
            int h = Math.Min(groupDim, height - y0);
            var rng = new Xorshift128Plus(visible, nonVisible, (uint)x0, (uint)y0);
            for (int c = 0; c < 3; c++)
            {
                FillRect(ref rng, planes[c], width, x0, y0, w, h);
            }
        });
    }

    // Floats come from batches of 16: whole batches while more than a batch remains in the row, then one batch for the tail.
    private static void FillRect(ref Xorshift128Plus rng, float[] plane, int stride, int x0, int y0, int w, int h)
    {
        const int floatsPerBatch = Xorshift128Plus.Lanes * 2;
        Span<ulong> batch64 = stackalloc ulong[Xorshift128Plus.Lanes];
        Span<uint> batch32 = stackalloc uint[floatsPerBatch];
        for (int y = 0; y < h; y++)
        {
            int row = ((y0 + y) * stride) + x0;
            int x = 0;
            for (; x + floatsPerBatch < w; x += floatsPerBatch)
            {
                rng.Fill(batch64);
                Split(batch64, batch32);
                for (int i = 0; i < floatsPerBatch; i++)
                {
                    plane[row + x + i] = BitsToFloat(batch32[i]);
                }
            }

            rng.Fill(batch64);
            Split(batch64, batch32);
            for (int i = 0; x + i < w; i++)
            {
                plane[row + x + i] = BitsToFloat(batch32[i]);
            }
        }
    }

    private static void Split(ReadOnlySpan<ulong> from, Span<uint> to)
    {
        for (int i = 0; i < from.Length; i++)
        {
            to[2 * i] = (uint)from[i];
            to[(2 * i) + 1] = (uint)(from[i] >> 32);
        }
    }

    // 1.0 plus 23 random mantissa bits: a value in [1, 2).
    private static float BitsToFloat(uint bits) => BitConverter.UInt32BitsToSingle((bits >> 9) | 0x3F800000u);

    /// <summary>Eight interleaved xorshift128+ generators, seeded through SplitMix64.</summary>
    private struct Xorshift128Plus
    {
        public const int Lanes = 8;
        private readonly ulong[] _s0;
        private readonly ulong[] _s1;

        public Xorshift128Plus(uint seed1, uint seed2, uint seed3, uint seed4)
        {
            _s0 = new ulong[Lanes];
            _s1 = new ulong[Lanes];
            _s0[0] = SplitMix64((((ulong)seed1 << 32) + seed2) + GoldenGamma);
            _s1[0] = SplitMix64((((ulong)seed3 << 32) + seed4) + GoldenGamma);
            for (int i = 1; i < Lanes; i++)
            {
                _s0[i] = SplitMix64(_s0[i - 1]);
                _s1[i] = SplitMix64(_s1[i - 1]);
            }
        }

        public readonly void Fill(Span<ulong> randomBits)
        {
            for (int i = 0; i < Lanes; i++)
            {
                ulong s1 = _s0[i];
                ulong s0 = _s1[i];
                randomBits[i] = s1 + s0;
                _s0[i] = s0;
                s1 ^= s1 << 23;
                s1 ^= s0 ^ (s1 >> 18) ^ (s0 >> 5);
                _s1[i] = s1;
            }
        }

        private static ulong SplitMix64(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
