using PeachImage.Formats.Webp.Decoding.Vp8.LoopFilter;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Pins the vectorized horizontal-edge loop filter against the scalar per-lane filter it replaces.
/// </summary>
/// <remarks>
/// The filter is a cascade of gated choices — an edge-strength gate, an interior-difference gate, then a
/// three-way pick between the 2-, 4- and 6-tap filters — so what has to be exercised is the <em>boundaries</em>
/// of those gates, not typical pixel values. Random planes almost never sit on one. The sweeps below therefore
/// walk thresholds across their whole range and construct lanes whose differences land exactly on, just below,
/// and just above each limit.
/// </remarks>
public class Vp8VectorLoopFilterTests
{
    private const int Stride = 32;
    // Tall enough for 16 lanes a full stride apart (origin + 15*stride + 4 taps), which the strided
    // orientation needs; the contiguous one fits comfortably inside the same buffer.
    private const int Rows = 24;
    private const int Origin = 4 * Stride;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void VectorFilter_MatchesScalarFilter_AcrossThresholdSweeps(bool macroblockEdge, bool strided)
    {
        SkipIfUnsupported(strided);

        foreach (int thresh in new[] { 0, 1, 2, 7, 8, 20, 63, 64, 100, 127, 128, 189, 193 })
        {
            foreach (int interiorLimit in new[] { 0, 1, 2, 9, 32, 63 })
            {
                foreach (int hevThreshold in new[] { 0, 1, 2 })
                {
                    for (int seed = 0; seed < 6; seed++)
                    {
                        AssertAgrees(MakePlane(seed, spread: seed % 3), thresh, interiorLimit, hevThreshold, macroblockEdge, strided);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every lane set to a flat value with one tap perturbed by a controlled delta, so the edge-strength and
    /// interior gates are crossed one step at a time rather than by luck.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void VectorFilter_MatchesScalarFilter_WhenDifferencesSitOnGateBoundaries(bool macroblockEdge, bool strided)
    {
        SkipIfUnsupported(strided);

        for (int tap = 0; tap < 8; tap++)
        {
            for (int delta = -20; delta <= 20; delta++)
            {
                byte[] plane = new byte[Stride * Rows];
                Array.Fill(plane, (byte)128);

                for (int lane = 0; lane < 16; lane++)
                {
                    int offset = strided
                        ? Origin + (tap - 4) + (lane * Stride)
                        : Origin + ((tap - 4) * Stride) + lane;
                    plane[offset] = (byte)Math.Clamp(128 + delta + lane - 8, 0, 255);
                }

                foreach (int thresh in new[] { 0, 5, 20, 63, 189 })
                {
                    foreach (int interiorLimit in new[] { 1, 4, 16, 63 })
                    {
                        foreach (int hevThreshold in new[] { 0, 1, 2 })
                        {
                            AssertAgrees(plane, thresh, interiorLimit, hevThreshold, macroblockEdge, strided);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Saturating extremes: taps pinned at 0 and 255 drive every clamp in the filter arithmetic to its limit.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void VectorFilter_MatchesScalarFilter_AtChannelExtremes(bool macroblockEdge, bool strided)
    {
        SkipIfUnsupported(strided);

        var random = new Random(90210);

        for (int trial = 0; trial < 400; trial++)
        {
            byte[] plane = new byte[Stride * Rows];
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] = random.Next(3) switch
                {
                    0 => (byte)0,
                    1 => (byte)255,
                    _ => (byte)random.Next(256),
                };
            }

            AssertAgrees(plane, random.Next(190), random.Next(64), random.Next(3), macroblockEdge, strided);
        }
    }

    private static byte[] MakePlane(int seed, int spread)
    {
        var random = new Random(seed);
        byte[] plane = new byte[Stride * Rows];

        // spread 0: near-flat (gates pass), 1: moderate, 2: wild (gates fail). All three matter -- the filter
        // is a no-op on one side of the gate and a six-tap rewrite on the other.
        int amplitude = spread switch { 0 => 3, 1 => 24, _ => 255 };

        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = (byte)Math.Clamp(128 + random.Next(-amplitude, amplitude + 1), 0, 255);
        }

        return plane;
    }

    private static void SkipIfUnsupported(bool strided)
    {
        Assert.SkipUnless(
            strided
                ? Vp8VectorLoopFilter.CanFilterStrided(16, Origin, Stride, Stride * Rows)
                : Vp8VectorLoopFilter.CanFilter(16, Origin, Stride, Stride * Rows),
            strided ? "SSE2 is not available here." : "Vector128 is not hardware accelerated here.");
    }

    /// <summary>
    /// Drives the production filter and an independent scalar transliteration over the same plane and compares
    /// the whole buffer. The two orientations differ only in which axis the taps run along, so the same
    /// reference serves both — with <c>acrossStep</c> swapped between 1 and the stride.
    /// </summary>
    private static void AssertAgrees(byte[] plane, int thresh, int interiorLimit, int hevThreshold, bool macroblockEdge, bool strided)
    {
        byte[] fromVector = (byte[])plane.Clone();
        byte[] fromScalar = (byte[])plane.Clone();

        if (macroblockEdge)
        {
            if (strided)
            {
                Vp8NormalLoopFilter.FilterLeftEdge16(fromVector, Origin, Stride, thresh, interiorLimit, hevThreshold);
            }
            else
            {
                Vp8NormalLoopFilter.FilterTopEdge16(fromVector, Origin, Stride, thresh, interiorLimit, hevThreshold);
            }
        }
        else if (strided)
        {
            Vp8NormalLoopFilter.FilterLeftEdgeInner16(fromVector, Origin, Stride, thresh, interiorLimit, hevThreshold);
        }
        else
        {
            Vp8NormalLoopFilter.FilterTopEdgeInner16(fromVector, Origin, Stride, thresh, interiorLimit, hevThreshold);
        }

        ScalarReference(
            fromScalar,
            Origin,
            acrossStep: strided ? 1 : Stride,
            alongStep: strided ? Stride : 1,
            thresh,
            interiorLimit,
            hevThreshold,
            macroblockEdge);

        Assert.Equal(fromScalar, fromVector);
    }

    /// <summary>
    /// A transliteration of the per-lane scalar filter, independent of the production code's dispatch so the
    /// vector path cannot accidentally be compared against itself.
    /// </summary>
    private static void ScalarReference(byte[] p, int origin, int acrossStep, int alongStep, int thresh, int interiorLimit, int hevThresh, bool macroblockEdge)
    {
        int thresh2 = (2 * thresh) + 1;

        for (int i = 0; i < 16; i++)
        {
            int pos = origin + (i * alongStep);
            int step = acrossStep;

            int p3 = p[pos - (4 * step)], p2 = p[pos - (3 * step)], p1 = p[pos - (2 * step)], p0 = p[pos - step];
            int q0 = p[pos], q1 = p[pos + step], q2 = p[pos + (2 * step)], q3 = p[pos + (3 * step)];

            bool needsFilter = (4 * Math.Abs(p0 - q0)) + Math.Abs(p1 - q1) <= thresh2
                && Math.Abs(p3 - p2) <= interiorLimit && Math.Abs(p2 - p1) <= interiorLimit && Math.Abs(p1 - p0) <= interiorLimit
                && Math.Abs(q3 - q2) <= interiorLimit && Math.Abs(q2 - q1) <= interiorLimit && Math.Abs(q1 - q0) <= interiorLimit;

            if (!needsFilter)
            {
                continue;
            }

            if (Math.Abs(p1 - p0) > hevThresh || Math.Abs(q1 - q0) > hevThresh)
            {
                int a = (3 * (q0 - p0)) + SClip1(p1 - q1);
                p[pos - step] = Clip1(p0 + SClip2((a + 3) >> 3));
                p[pos] = Clip1(q0 - SClip2((a + 4) >> 3));
            }
            else if (macroblockEdge)
            {
                int a = SClip1((3 * (q0 - p0)) + SClip1(p1 - q1));
                int a1 = ((27 * a) + 63) >> 7;
                int a2 = ((18 * a) + 63) >> 7;
                int a3 = ((9 * a) + 63) >> 7;

                p[pos - (3 * step)] = Clip1(p2 + a3);
                p[pos - (2 * step)] = Clip1(p1 + a2);
                p[pos - step] = Clip1(p0 + a1);
                p[pos] = Clip1(q0 - a1);
                p[pos + step] = Clip1(q1 - a2);
                p[pos + (2 * step)] = Clip1(q2 - a3);
            }
            else
            {
                int a = 3 * (q0 - p0);
                int a1 = SClip2((a + 4) >> 3);
                int a2 = SClip2((a + 3) >> 3);
                int a3 = (a1 + 1) >> 1;

                p[pos - (2 * step)] = Clip1(p1 + a3);
                p[pos - step] = Clip1(p0 + a2);
                p[pos] = Clip1(q0 - a1);
                p[pos + step] = Clip1(q1 - a3);
            }
        }
    }

    private static byte Clip1(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    private static int SClip1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;

    private static int SClip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;
}
