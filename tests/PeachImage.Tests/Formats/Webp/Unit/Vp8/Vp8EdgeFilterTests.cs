using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PeachImage.Formats.Webp.Decoding.Vp8.LoopFilter;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// <see cref="Vp8EdgeFilter"/>, the decoder's whole-edge drivers, against an independent per-lane scalar transliteration of the
/// filter: the whole plane is compared after each edge, so a lane written outside the edge (or a chroma plane swapped with the
/// other) shows up as a difference.
/// </summary>
public class Vp8EdgeFilterTests
{
    private const int Stride = 40;
    private const int Rows = 40;
    private const int Origin = (8 * Stride) + 8;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void LumaEdge_MatchesScalarFilter(bool macroblockEdge, bool vertical)
    {
        Assert.SkipUnless(Vp8EdgeFilter.IsSupported, "SSE2 is not available here.");

        foreach (var (plane, thresh, interiorLimit, hevThreshold) in Cases(seed: 31))
        {
            byte[] fromVector = (byte[])plane.Clone();
            byte[] fromScalar = (byte[])plane.Clone();

            ref byte at = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(fromVector), Origin);
            if (vertical)
            {
                Vp8EdgeFilter.LumaVertical(ref at, Stride, macroblockEdge, thresh, interiorLimit, hevThreshold);
            }
            else
            {
                Vp8EdgeFilter.LumaHorizontal(ref at, Stride, macroblockEdge, thresh, interiorLimit, hevThreshold);
            }

            ScalarReference(fromScalar, 16, vertical, thresh, interiorLimit, hevThreshold, macroblockEdge);
            Assert.Equal(fromScalar, fromVector);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ChromaEdge_FiltersBothPlanesLikeTheScalarFilter(bool macroblockEdge, bool vertical)
    {
        Assert.SkipUnless(Vp8EdgeFilter.IsSupported, "SSE2 is not available here.");

        // U and V get different content from consecutive cases, so a swapped or shared half would not cancel out.
        var cases = Cases(seed: 977).ToArray();
        for (int i = 0; i + 1 < cases.Length; i += 2)
        {
            var (uPlane, thresh, interiorLimit, hevThreshold) = cases[i];
            byte[] vPlane = cases[i + 1].Plane;

            byte[] uVector = (byte[])uPlane.Clone();
            byte[] vVector = (byte[])vPlane.Clone();
            byte[] uScalar = (byte[])uPlane.Clone();
            byte[] vScalar = (byte[])vPlane.Clone();

            ref byte u = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(uVector), Origin);
            ref byte v = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(vVector), Origin);
            if (vertical)
            {
                Vp8EdgeFilter.ChromaVertical(ref u, ref v, Stride, macroblockEdge, thresh, interiorLimit, hevThreshold);
            }
            else
            {
                Vp8EdgeFilter.ChromaHorizontal(ref u, ref v, Stride, macroblockEdge, thresh, interiorLimit, hevThreshold);
            }

            ScalarReference(uScalar, 8, vertical, thresh, interiorLimit, hevThreshold, macroblockEdge);
            ScalarReference(vScalar, 8, vertical, thresh, interiorLimit, hevThreshold, macroblockEdge);
            Assert.Equal(uScalar, uVector);
            Assert.Equal(vScalar, vVector);
        }
    }

    /// <summary>Planes of near-flat, moderate and wild content crossed with thresholds either side of the filter's gates.</summary>
    private static IEnumerable<(byte[] Plane, int Thresh, int InteriorLimit, int HevThreshold)> Cases(int seed)
    {
        var random = new Random(seed);
        foreach (int thresh in new[] { 0, 1, 5, 20, 63, 127, 189 })
        {
            foreach (int interiorLimit in new[] { 0, 1, 4, 16, 63 })
            {
                foreach (int hevThreshold in new[] { 0, 1, 2 })
                {
                    for (int trial = 0; trial < 6; trial++)
                    {
                        int amplitude = trial % 3 switch { 0 => 3, 1 => 24, _ => 255 };
                        byte[] plane = new byte[Stride * Rows];
                        for (int i = 0; i < plane.Length; i++)
                        {
                            plane[i] = (byte)Math.Clamp(128 + random.Next(-amplitude, amplitude + 1), 0, 255);
                        }

                        yield return (plane, thresh, interiorLimit, hevThreshold);
                    }
                }
            }
        }
    }

    /// <summary>A transliteration of libwebp's per-lane filter, independent of the production code so the vector path cannot be compared with itself.</summary>
    private static void ScalarReference(byte[] p, int lanes, bool vertical, int thresh, int interiorLimit, int hevThresh, bool macroblockEdge)
    {
        int acrossStep = vertical ? 1 : Stride;
        int alongStep = vertical ? Stride : 1;
        int thresh2 = (2 * thresh) + 1;

        for (int i = 0; i < lanes; i++)
        {
            int pos = Origin + (i * alongStep);
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
