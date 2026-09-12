using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Port of libaom's own <c>dr_prediction_test.cc</c> (<c>DrPredTest::RunTest</c>/<c>RundrPredTest</c>/
/// <c>SaturatedValues</c>) and <c>intra_edge_test.cc</c> together, since the two real test files exercise the
/// same directional-prediction pipeline from opposite ends (the core z1/z2/z3 interpolation vs. the
/// edge-filter/upsample preprocessing that feeds it) and PeachImage's own production code
/// (<see cref="Av1IntraPrediction.Predict"/>'s directional path) fuses both into one call. See
/// <see cref="LibaomReferenceDrPrediction"/>'s own remarks for why this port compares a full assembled
/// pipeline rather than literally replaying <c>dr_prediction_test.cc</c>'s own SIMD-vs-C harness (this
/// project has no second SIMD implementation to compare against, so a C-vs-C check would be vacuous here).
///
/// <para><b>Scope reduction</b>: the real <c>dr_prediction_test.cc</c> sweeps all 90 angles per zone
/// (effectively every 1-degree <c>dr_intra_derivative</c> table entry, not just the 7 coding-legal
/// <c>angle_delta</c> values) across all 19 real AV1 transform sizes (4x4 up to 64x64 and every rectangular
/// ratio up to 4:1). This port instead covers, per the task's own explicit reduction allowance: each of the 8
/// real base angles (45/67/90/113/135/157/180/203, i.e. every directional <see cref="Av1IntraMode"/>) at the
/// coding-legal <c>angle_delta</c> extremes and center (-3, 0, +3, in the real 3-degree <c>ANGLE_STEP</c>
/// units) -- spanning all three z1/z2/z3 zones plus both exact-90/exact-180 straight-copy cases -- across
/// five representative block sizes (4x4, 8x8, 4x8, 8x4 per the task's own suggestion, plus 16x16 so at least
/// one size crosses the <c>w + h &gt;= 24</c> threshold that gates <c>filter_intra_edge_corner</c> and the
/// higher edge-filter-strength/upsample-selection bands -- sizes below that threshold, which 4x4/8x8/4x8/8x4
/// all are, never exercise that corner case at all), each combination run both with edge filtering/upsampling
/// disabled and enabled (both edge-filter types, matching real <c>intra_edge_filter_type</c> 0 and 1), against
/// <see cref="CountIterations"/> draws of random 8-bit neighbor data plus a dedicated saturated-value case
/// mirroring the real <c>SaturatedValues</c> test.</para>
/// </summary>
public class Av1DrPredictionTests
{
    private const int CountIterations = 20;

    /// <summary>
    /// Offset (the array index standing for conceptual index 0) into the raw above/left buffers. Generous
    /// enough for any tested block size's real <c>[-2, 2*(w+h)-2]</c> read/write extent (the upsample
    /// process's own extent) -- the largest tested <c>w+h</c> is 32 (16x16), so 62 is the real upper bound;
    /// 64 leaves headroom.
    /// </summary>
    private const int EdgeOffset = 64;

    private const int EdgeCapacity = EdgeOffset + 128;

    private static readonly (string Name, int Mode)[] DirectionalModes =
    [
        ("D45_PRED", Av1IntraMode.D45Pred),
        ("D67_PRED", Av1IntraMode.D67Pred),
        ("V_PRED", Av1IntraMode.VPred),
        ("D113_PRED", Av1IntraMode.D113Pred),
        ("D135_PRED", Av1IntraMode.D135Pred),
        ("D157_PRED", Av1IntraMode.D157Pred),
        ("H_PRED", Av1IntraMode.HPred),
        ("D203_PRED", Av1IntraMode.D203Pred),
    ];

    private static readonly int[] AngleDeltas = [-3, 0, 3];

    private static readonly (int W, int H)[] Sizes = [(4, 4), (8, 8), (4, 8), (8, 4), (16, 16)];

    /// <summary><c>intra_edge_filter_type</c> 0 and 1 (whichever of a block's above/left neighbor blocks was
    /// coded with a SMOOTH_* mode), both with edge filtering disabled entirely (the real
    /// <c>disable_edge_filter</c>/<c>lossless</c> path).</summary>
    private static readonly (bool Enable, bool Smooth)[] EdgeFilterCases = [(false, false), (true, false), (true, true)];

    public static IEnumerable<object[]> Cases()
    {
        foreach (var (name, mode) in DirectionalModes)
        {
            foreach (int delta in AngleDeltas)
            {
                foreach (var (w, h) in Sizes)
                {
                    foreach (var (enable, smooth) in EdgeFilterCases)
                    {
                        yield return [name, mode, delta, w, h, enable, smooth];
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Directional_MatchesLibaomReference(string modeName, int mode, int angleDelta, int w, int h, bool enableEdgeFilter, bool filterTypeSmooth)
    {
        // Seeded independently per case so this test's own total draw count/ordering doesn't shift a case's
        // random data if another case is added or removed later (matching this project's established
        // per-combination seeding practice -- see Av1FilterIntraPredictionTests's own remarks).
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);

        for (int iter = 0; iter < CountIterations; iter++)
        {
            RunOne(rnd, modeName, mode, angleDelta, w, h, enableEdgeFilter, filterTypeSmooth, iter, saturated: false);
        }
    }

    /// <summary>Mirrors the real <c>DrPredTest::SaturatedValues</c> test: every above/left neighbor pixel
    /// (including the corner) at the maximum 8-bit value, the pathological input that test specifically
    /// targets (the "clamp to the last valid sample" tail logic in z1/z3, and the interpolation's own
    /// rounding at a perfectly flat input, both being places an off-by-one would otherwise hide among
    /// generic random data).</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Directional_SaturatedValues_MatchesLibaomReference(string modeName, int mode, int angleDelta, int w, int h, bool enableEdgeFilter, bool filterTypeSmooth)
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        RunOne(rnd, modeName, mode, angleDelta, w, h, enableEdgeFilter, filterTypeSmooth, iter: 0, saturated: true);
    }

    private static void RunOne(LibaomAcmRandom rnd, string modeName, int mode, int angleDelta, int w, int h, bool enableEdgeFilter, bool filterTypeSmooth, int iter, bool saturated)
    {
        // Independent reference-side buffers (offset-indexed per LibaomReferenceDrPrediction's own contract).
        var aboveRef = new int[EdgeCapacity];
        var leftRef = new int[EdgeCapacity];

        // Production-side buffers (Av1EdgeArray's own [-2, capacity) contract).
        var aboveProd = new Av1EdgeArray(EdgeCapacity - 2);
        var leftProd = new Av1EdgeArray(EdgeCapacity - 2);

        // Upper bound on how many above/left samples either side ever needs, even after upsampling doubles
        // the range (see LibaomReferenceDrPrediction's own EdgeOffset remarks above).
        int span = w + h;
        for (int i = -1; i < (2 * span); i++)
        {
            int v = saturated ? 255 : rnd.Rand8();
            aboveRef[EdgeOffset + i] = v;
            aboveProd[i] = v;

            int v2 = saturated ? 255 : rnd.Rand8();
            leftRef[EdgeOffset + i] = v2;
            leftProd[i] = v2;
        }

        var expected = new int[w * h];
        var actual = new int[w * h];

        LibaomReferenceDrPrediction.Predict(expected, w, h, aboveRef, EdgeOffset, leftRef, EdgeOffset, mode, angleDelta, enableEdgeFilter, filterTypeSmooth);

        Av1IntraPrediction.Predict(
            actual, w, h, log2W: Log2(w), log2H: Log2(h), aboveProd, leftProd, mode,
            haveLeft: true, haveAbove: true, useFilterIntra: false, filterIntraMode: 0, angleDelta: angleDelta,
            enableIntraEdgeFilter: enableEdgeFilter, filterTypeSmooth: filterTypeSmooth,
            maxX: w - 1, maxY: h - 1, x: 0, y: 0, bitDepth: 8);

        for (int i = 0; i < w * h; i++)
        {
            Assert.True(
                expected[i] == actual[i],
                $"{modeName} angleDelta={angleDelta} {w}x{h} edgeFilter={enableEdgeFilter} smooth={filterTypeSmooth} saturated={saturated} iter={iter}, position {i}: expected {expected[i]}, actual {actual[i]}");
        }
    }

    private static int Log2(int value)
    {
        int result = 0;
        while ((1 << result) < value)
        {
            result++;
        }

        return result;
    }
}
