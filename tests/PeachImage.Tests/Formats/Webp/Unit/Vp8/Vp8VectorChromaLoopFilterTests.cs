using PeachImage.Formats.Webp.Decoding.Vp8.LoopFilter;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// The 8-lane (chroma) counterpart of <see cref="Vp8VectorLoopFilterTests"/>, over both edge orientations.
/// </summary>
/// <remarks>
/// Chroma edges are half a vector wide and take different paths through the kernel than 16-lane luma edges do:
/// the contiguous form runs one filter pass against 8-byte loads instead of two against 16-byte ones, and the
/// strided form is exactly the 8x8 block the transpose ladder's first stage already produces. Both are worth
/// their own coverage — a half-vector path that quietly filtered 16 lanes, or read the wrong half, would
/// corrupt the neighbouring subblock rather than the one under test, which the whole-buffer comparison here
/// catches.
/// </remarks>
public class Vp8VectorChromaLoopFilterTests
{
    private const int Lanes = 8;
    private const int Stride = 32;
    private const int Rows = 24;
    private const int Origin = 4 * Stride;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ChromaFilter_MatchesScalarFilter_AcrossThresholdSweeps(bool macroblockEdge, bool strided)
    {
        SkipIfUnsupported(strided);

        var random = new Random(515);

        foreach (int thresh in new[] { 0, 1, 5, 20, 63, 127, 189 })
        {
            foreach (int interiorLimit in new[] { 0, 1, 4, 16, 63 })
            {
                foreach (int hevThreshold in new[] { 0, 1, 2 })
                {
                    for (int trial = 0; trial < 8; trial++)
                    {
                        // Near-flat, moderate and wild planes in rotation: the filter is a no-op on one side of
                        // its gates and a six-tap rewrite on the other, and both sides need exercising.
                        int amplitude = trial % 3 switch { 0 => 3, 1 => 24, _ => 255 };

                        byte[] plane = new byte[Stride * Rows];
                        for (int i = 0; i < plane.Length; i++)
                        {
                            plane[i] = (byte)Math.Clamp(128 + random.Next(-amplitude, amplitude + 1), 0, 255);
                        }

                        AssertAgrees(plane, thresh, interiorLimit, hevThreshold, macroblockEdge, strided);
                    }
                }
            }
        }
    }

    /// <summary>
    /// With the gates wide open the filter fires on all eight lanes, so this is where a half-vector path that
    /// wrote a full vector's worth would show up — as a difference outside the eight lanes under test.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ChromaFilter_LeavesNeighbouringLanesUntouched(bool macroblockEdge, bool strided)
    {
        SkipIfUnsupported(strided);

        var random = new Random(2718);
        byte[] plane = new byte[Stride * Rows];
        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = (byte)Math.Clamp(128 + random.Next(-4, 5), 0, 255);
        }

        AssertAgrees(plane, thresh: 189, interiorLimit: 63, hevThreshold: 0, macroblockEdge, strided);
    }

    private static void SkipIfUnsupported(bool strided) =>
        Assert.SkipUnless(
            strided
                ? Vp8VectorLoopFilter.CanFilterStrided(Lanes, Origin, Stride, Stride * Rows)
                : Vp8VectorLoopFilter.CanFilter(Lanes, Origin, Stride, Stride * Rows),
            "The required vector support is not available here.");

    private static void AssertAgrees(byte[] plane, int thresh, int interiorLimit, int hevThreshold, bool macroblockEdge, bool strided)
    {
        byte[] fromVector = (byte[])plane.Clone();
        byte[] fromScalar = (byte[])plane.Clone();

        // The inner-edge entry points step to the subblock edge themselves, so the reference has to start there too.
        int referenceOrigin = Origin;

        if (macroblockEdge)
        {
            if (strided)
            {
                Vp8NormalLoopFilter.FilterLeftEdge8(fromVector, Origin, Stride, thresh, interiorLimit, hevThreshold);
            }
            else
            {
                Vp8NormalLoopFilter.FilterTopEdge8(fromVector, Origin, Stride, thresh, interiorLimit, hevThreshold);
            }
        }
        else if (strided)
        {
            Vp8NormalLoopFilter.FilterLeftEdgeInner8(fromVector, Origin, Stride, thresh, interiorLimit, hevThreshold);
            referenceOrigin = Origin + 4;
        }
        else
        {
            Vp8NormalLoopFilter.FilterTopEdgeInner8(fromVector, Origin, Stride, thresh, interiorLimit, hevThreshold);
            referenceOrigin = Origin + (4 * Stride);
        }

        ScalarReference(
            fromScalar,
            referenceOrigin,
            acrossStep: strided ? 1 : Stride,
            alongStep: strided ? Stride : 1,
            thresh,
            interiorLimit,
            hevThreshold,
            macroblockEdge);

        Assert.Equal(fromScalar, fromVector);
    }

    /// <summary>Eight lanes of the same transliterated scalar filter the 16-lane tests use, held here independently of the production code.</summary>
    private static void ScalarReference(byte[] p, int origin, int acrossStep, int alongStep, int thresh, int interiorLimit, int hevThresh, bool macroblockEdge)
    {
        int thresh2 = (2 * thresh) + 1;

        for (int i = 0; i < Lanes; i++)
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
