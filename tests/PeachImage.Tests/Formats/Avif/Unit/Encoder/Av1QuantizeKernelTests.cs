using System.Runtime.Intrinsics;
using PeachImage.Formats.Avif.Encoder.Av1.Quantization;
using PeachImage.Tests.Formats.Avif.Unit;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Vector128Av1QuantizeKernel"/>/<see cref="Vector256Av1QuantizeKernel"/> agree exactly
/// with <see cref="ScalarAv1QuantizeKernel"/> (all three currently share the same real per-coefficient
/// formula -- see <see cref="Vector128Av1QuantizeKernel"/>'s own remarks for why the SIMD tiers aren't yet
/// genuinely vectorized), and pins specific edge cases of the real fixed-point algorithm
/// (<see cref="ScalarAv1QuantizeKernel.QuantizeOne"/>, libaom's own real <c>av1_quantize_fp_no_qmatrix</c>)
/// this project's own encoder didn't previously implement: the hard zero-bin deadzone and the
/// <c>log_scale</c> (32x32-transform) path.
///
/// <para><b>CoeffCheck_MatchesLibaomReference</b> (project plan's own libaom test-port item, and the direct
/// motivation for replacing this project's own former reciprocal-multiply quantizer approximation with a
/// real port of <c>av1_quantize_fp_no_qmatrix</c>): a faithful port of libaom's own
/// <c>av1_quantize_test.cc</c> <c>AV1QuantizeTest.RunQuantizeTest</c> methodology -- deterministic random
/// coefficients and dequant steps (matching that real test's own <c>coeffRange</c>/<c>dequantRange</c>),
/// checked against <see cref="LibaomReferenceQuantize"/> (an independent transcription of libaom's own real
/// formula, not derived from <see cref="ScalarAv1QuantizeKernel"/>'s own implementation).</para>
/// </summary>
public class Av1QuantizeKernelTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Vector128Quantize_MatchesScalarReferenceExactly(int size)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("No 128-bit SIMD hardware acceleration available on this machine.");
        }

        AssertAgree(new ScalarAv1QuantizeKernel(), new Vector128Av1QuantizeKernel(), size);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Vector256Quantize_MatchesScalarReferenceExactly(int size)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("No 256-bit SIMD hardware acceleration (AVX/AVX2) available on this machine.");
        }

        AssertAgree(new ScalarAv1QuantizeKernel(), new Vector256Av1QuantizeKernel(), size);
    }

    /// <summary>
    /// libaom's own real hard zero-bin: a coefficient whose magnitude, doubled, still falls short of the
    /// dequant step is dropped to exactly 0 before the multiply-shift even runs -- distinct from ordinary
    /// "round to nearest," which a coefficient just below this deadzone could still round up to a nonzero
    /// level under. Uses <c>|coeff| = 1000</c> (comfortably clear of the deadzone boundary) for the
    /// "must not zero out" case rather than the boundary value itself: right at
    /// <c>|coeff| == dequant / 2</c>, the deadzone check alone passes, but the real algorithm's own
    /// subsequent fixed-point multiply-shift can still legitimately floor the result to 0 anyway (both
    /// libaom's own real code and this port explicitly handle that as "no coefficient written," matching
    /// exactly) -- that's real, correct behavior, not a boundary this test should assert against.
    /// </summary>
    [Fact]
    public void QuantizeOne_BelowDeadzone_RoundsToZero()
    {
        // dequant = 100, so the deadzone threshold is dequant/2 = 50 (logScale = 0): |coeff| = 49 must zero
        // out (49 &lt;&lt; 1 = 98 &lt; 100).
        Assert.Equal(0, ScalarAv1QuantizeKernel.QuantizeOne(49, quantFp: (1 << 16) / 100, roundFp: (64 * 100) >> 7, dequant: 100, logScale: 0));
        Assert.Equal(0, ScalarAv1QuantizeKernel.QuantizeOne(-49, quantFp: (1 << 16) / 100, roundFp: (64 * 100) >> 7, dequant: 100, logScale: 0));
        Assert.NotEqual(0, ScalarAv1QuantizeKernel.QuantizeOne(1000, quantFp: (1 << 16) / 100, roundFp: (64 * 100) >> 7, dequant: 100, logScale: 0));
    }

    /// <summary>Sign is restored exactly (not just magnitude), matching libaom's own real <c>AOMSIGN</c>-based restoration.</summary>
    [Fact]
    public void QuantizeOne_PreservesSign()
    {
        int quantFp = (1 << 16) / 4;
        int roundFp = (64 * 4) >> 7;

        int positive = ScalarAv1QuantizeKernel.QuantizeOne(400, quantFp, roundFp, dequant: 4, logScale: 0);
        int negative = ScalarAv1QuantizeKernel.QuantizeOne(-400, quantFp, roundFp, dequant: 4, logScale: 0);

        Assert.Equal(100, positive);
        Assert.Equal(-100, negative);
    }

    /// <summary>
    /// This project's own real, currently-reachable production case: lossless (<c>base_q_idx == 0</c>,
    /// <c>dequant == 4</c>), where every WHT-scaled coefficient is an exact multiple of 4 -- the real
    /// fixed-point formula must reduce to plain exact division by 4 here (no deadzone/rounding ever
    /// triggers for a value already divisible by the quantizer step, as long as the value stays within the
    /// real algorithm's own <c>int16_t</c> clamp range -- see
    /// <see cref="QuantizeOne_ExtremeLosslessCoefficient_ClampsToInt16RangeLikeRealLibaom"/> for the one
    /// case where it doesn't), confirming this port is a genuine zero-behavior-change fix for this
    /// project's own only active encode path over the realistic coefficient range.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 1)]
    [InlineData(-4, -1)]
    [InlineData(400, 100)]
    [InlineData(-4000, -1000)]
    public void QuantizeOne_LosslessQIndexZero_MatchesExactDivisionByFour(int coeffValue, int expected)
    {
        int quantFp = (1 << 16) / 4;
        int roundFp = (64 * 4) >> 7;

        Assert.Equal(expected, ScalarAv1QuantizeKernel.QuantizeOne(coeffValue, quantFp, roundFp, dequant: 4, logScale: 0));
    }

    /// <summary>
    /// A genuine, real edge case found while porting this algorithm: <see cref="Av1ForwardWht"/>'s own
    /// documented magnitude bound (<c>4 * 16384 = 65536</c>, see <c>Av1ForwardWhtTests</c>'s own
    /// <c>MemCheck_*</c> tests) can reach a WHT coefficient this large in an extreme, hand-constructed
    /// all-same-value input -- large enough that libaom's own real <c>clamp64(abs_coeff + rounding,
    /// INT16_MIN, INT16_MAX)</c> clips it to 32767 (or -32768) <em>before</em> the multiply, rather than
    /// quantizing it exactly. This project's own old reciprocal-multiply approximation had no such clamp at
    /// all, so this is a real, previously-nonexistent boundary this port now reproduces faithfully (matching
    /// libaom's own real, deliberate behavior -- not a bug to work around).
    /// </summary>
    [Fact]
    public void QuantizeOne_ExtremeLosslessCoefficient_ClampsToInt16RangeLikeRealLibaom()
    {
        int quantFp = (1 << 16) / 4;
        int roundFp = (64 * 4) >> 7;

        int result = ScalarAv1QuantizeKernel.QuantizeOne(-65536, quantFp, roundFp, dequant: 4, logScale: 0);

        Assert.NotEqual(-16384, result); // NOT exact division by 4 -- the int16 clamp changes the result.
        Assert.Equal(-8191, result); // (clamp(65536 + 2, ..., 32767) * 16384) >> 16, sign-restored.
    }

    /// <summary><c>logScale == 1</c> (the real 32x32-transform path, <c>av1_build_quantizer</c>'s own <c>log_scale</c>): both the deadzone threshold and the final shift change.</summary>
    [Fact]
    public void QuantizeOne_LogScaleOne_MatchesRealFormula()
    {
        const int dequant = 100;
        const int quantFp = (1 << 16) / dequant;
        const int roundFp = (64 * dequant) >> 7;

        // Deadzone at logScale=1: |coeff| &lt;&lt; 2 &gt;= 100 -> |coeff| &gt;= 25 (below that, always 0).
        Assert.Equal(0, ScalarAv1QuantizeKernel.QuantizeOne(24, quantFp, roundFp, dequant, logScale: 1));

        // 1000 is comfortably clear of the deadzone-vs-multiply-shift-floor boundary (see
        // QuantizeOne_BelowDeadzone_RoundsToZero's own remarks on why the boundary value itself isn't a
        // reliable "must be nonzero" test).
        Assert.NotEqual(0, ScalarAv1QuantizeKernel.QuantizeOne(1000, quantFp, roundFp, dequant, logScale: 1));

        // rounding = ROUND_POWER_OF_TWO(roundFp, 1) = (roundFp + 1) >> 1; tmp32 = (abs+rounding)*quantFp >> 15.
        int rounding = (roundFp + 1) >> 1;
        long expectedAbs = (200L + rounding) * quantFp >> 15;
        Assert.Equal((int)expectedAbs, ScalarAv1QuantizeKernel.QuantizeOne(200, quantFp, roundFp, dequant, logScale: 1));
    }

    /// <summary>
    /// Port of libaom's own <c>av1_quantize_test.cc</c> <c>AV1QuantizeTest.RunQuantizeTest</c>: 1,000
    /// deterministic random blocks, each with its own random dequant step (matching that real test's own
    /// <c>coeffRange = (1&lt;&lt;20)-1</c>/<c>dequantRange = 32768</c> generation), compared against
    /// <see cref="LibaomReferenceQuantize"/> -- libaom's own real formula, independently transcribed, not
    /// <see cref="ScalarAv1QuantizeKernel"/>'s own implementation.
    /// </summary>
    [Fact]
    public void CoeffCheck_MatchesLibaomReference()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        const int countTestBlock = 1000;
        const int coeffRange = (1 << 20) - 1;
        const int dequantRange = 32768;
        const int size = 4;
        const int total = size * size;

        for (int b = 0; b < countTestBlock; b++)
        {
            var coeff = new int[total];
            for (int j = 0; j < total; j++)
            {
                coeff[j] = rnd.PseudoUniform(coeffRange);
            }

            int dcQ = Math.Abs(rnd.PseudoUniform(dequantRange));
            int acQ = Math.Abs(rnd.PseudoUniform(dequantRange));
            dcQ = Math.Max(dcQ, 1); // dequant == 0 is not a real quantizer step (division by zero).
            acQ = Math.Max(acQ, 1);

            var expected = new int[total];
            LibaomReferenceQuantize.Quantize(coeff, expected, size, dcQ, acQ, logScale: 0);

            (int quantFpDc, int roundFpDc) = LibaomReferenceQuantize.BuildQuantizer(dcQ);
            (int quantFpAc, int roundFpAc) = LibaomReferenceQuantize.BuildQuantizer(acQ);
            var actual = new int[total];
            Av1QuantizeKernelSelector.Instance.Quantize(coeff, actual, size, quantFpDc, quantFpAc, roundFpDc, roundFpAc, dcQ, acQ, logScale: 0);

            Assert.True(expected.AsSpan().SequenceEqual(actual), $"block {b}: dcQ={dcQ}, acQ={acQ}");
        }
    }

    private static void AssertAgree(IAv1QuantizeKernel scalar, IAv1QuantizeKernel simd, int size)
    {
        int total = size * size;
        var random = new Random(size * 104729);
        var coeff = new int[total];
        for (int i = 0; i < total; i++)
        {
            coeff[i] = random.Next(-4096, 4096);
        }

        const int dcQ = 17;
        const int acQ = 53;
        int logScale = size == 32 ? 1 : 0;
        int quantFpDc = (1 << 16) / dcQ;
        int quantFpAc = (1 << 16) / acQ;
        int roundFpDc = (64 * dcQ) >> 7;
        int roundFpAc = (64 * acQ) >> 7;

        var scalarOut = new int[total];
        var simdOut = new int[total];
        scalar.Quantize(coeff, scalarOut, size, quantFpDc, quantFpAc, roundFpDc, roundFpAc, dcQ, acQ, logScale);
        simd.Quantize(coeff, simdOut, size, quantFpDc, quantFpAc, roundFpDc, roundFpAc, dcQ, acQ, logScale);

        Assert.Equal(scalarOut, simdOut);
    }
}
