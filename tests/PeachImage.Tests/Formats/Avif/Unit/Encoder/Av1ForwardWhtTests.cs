using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;
using PeachImage.Tests.Formats.Avif.Unit;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1ForwardWht"/> round-trips exactly (not just approximately, unlike
/// <see cref="Av1ForwardTransformTests"/>'s DCT case) through the real, unmodified
/// <see cref="Av1InverseTransform.Inverse2D"/> at <c>lossless: true</c> -- lossless has no quantization step
/// to mask rounding error, so anything short of bit-exact would be a real correctness bug, not a tolerable
/// approximation.
///
/// <para><b>CoeffCheck_MatchesLibaomReference</b>/<b>MemCheck_ExtremeValues</b> (project plan's own libaom
/// test-port item): a faithful port of libaom's own <c>fwht4x4_test.cc</c>/<c>transform_test_base.h</c>
/// <c>CoeffCheck</c>/<c>MemCheck</c>, using <see cref="LibaomAcmRandom"/> (a bit-exact port of libaom's own
/// deterministic PRNG) so the test data is the *same* pseudo-random sequence the real libaom test feeds its
/// own reference, and <see cref="LibaomReferenceWht.Fwht4x4"/> (transcribed directly from
/// <c>av1/encoder/hybrid_fwd_txfm.c</c>'s own <c>av1_fwht4x4_c</c>, independent of
/// <see cref="Av1ForwardWht"/>'s own implementation) as the reference to compare against -- unlike the
/// round-trip tests above, which only prove forward and inverse are each other's exact algebraic inverse
/// (a self-consistently-wrong pair would still pass those), this proves the forward transform matches
/// libaom's own real algorithm independently.</para>
/// </summary>
public class Av1ForwardWhtTests
{
    [Fact]
    public void Forward4x4_ZeroResidual_ProducesZeroCoefficients()
    {
        int[] residual = new int[16];
        int[] coeff = new int[16];

        Av1ForwardWht.Forward4x4(residual, coeff);

        Assert.All(coeff, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(-128)]
    [InlineData(127)]
    public void Forward4x4_ConstantResidual_RoundTripsExactly(int constantValue)
    {
        int[] residual = new int[16];
        Array.Fill(residual, constantValue);

        AssertRoundTripsExactly(residual);
    }

    [Fact]
    public void Forward4x4_Gradient_RoundTripsExactly()
    {
        int[] residual = new int[16];
        for (int i = 0; i < 16; i++)
        {
            residual[i] = (i * 17) - 120; // spans negative and positive, not a multiple of 4
        }

        AssertRoundTripsExactly(residual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12345)]
    public void Forward4x4_RandomResiduals_RoundTripExactly(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 200; trial++)
        {
            int[] residual = new int[16];
            for (int i = 0; i < 16; i++)
            {
                residual[i] = random.Next(-255, 256); // a real 8-bit prediction residual's full range
            }

            AssertRoundTripsExactly(residual);
        }
    }

    [Fact]
    public void Forward4x4_SingleNonZeroSample_RoundTripsExactly()
    {
        for (int pos = 0; pos < 16; pos++)
        {
            int[] residual = new int[16];
            residual[pos] = 200;

            AssertRoundTripsExactly(residual);
        }
    }

    /// <summary>
    /// Port of libaom's own <c>fwht4x4_test.cc</c> <c>Trans4x4WHT.CoeffCheck</c> (via
    /// <c>transform_test_base.h</c>'s own <c>RunCoeffCheck</c>): 5,000 blocks of the same deterministic
    /// pseudo-random residual data libaom's own real test feeds <c>av1_fwht4x4_c</c> (an 8-bit range here,
    /// not libaom's own 10/12-bit <c>Trans4x4WHT</c> instantiations -- see this class's own remarks:
    /// the transform itself is bit-depth-independent pure integer arithmetic, and 8-bit is what this
    /// project's own encoder actually uses, so it's the representative range to port this test at), asserting
    /// <see cref="Av1ForwardWht.Forward4x4"/> is bit-exact against <see cref="LibaomReferenceWht.Fwht4x4"/> --
    /// libaom's own real algorithm, independently transcribed, not <see cref="Av1ForwardWht"/>'s own inverse.
    /// PeachImage's own <see cref="Av1ForwardWht.Forward4x4"/> has no stride parameter (always a tightly
    /// packed 4x4 block), so the reference is called with <c>stride: 4</c> rather than libaom's own
    /// deliberately-mismatched 96 -- the "wrong stride" hazard that guards against genuinely doesn't apply to
    /// a function with no stride concept to get wrong.
    /// </summary>
    [Fact]
    public void CoeffCheck_MatchesLibaomReference()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        const int countTestBlock = 5000;
        const int mask = 255; // AOM_BITS_8.

        for (int i = 0; i < countTestBlock; i++)
        {
            var input = new short[16];
            for (int j = 0; j < 16; j++)
            {
                input[j] = (short)((rnd.Rand16() & mask) - (rnd.Rand16() & mask));
            }

            var expected = new int[16];
            LibaomReferenceWht.Fwht4x4(input, expected, stride: 4);

            var residual = new int[16];
            for (int j = 0; j < 16; j++)
            {
                residual[j] = input[j];
            }

            var actual = new int[16];
            Av1ForwardWht.Forward4x4(residual, actual);

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// Port of libaom's own <c>fwht4x4_test.cc</c> <c>Trans4x4WHT.MemCheck</c> (via
    /// <c>transform_test_base.h</c>'s own <c>RunMemCheck</c>): extreme (+/-255, this project's own 8-bit
    /// range -- see <see cref="CoeffCheck_MatchesLibaomReference"/>'s remarks) input, verifying both that
    /// <see cref="Av1ForwardWht.Forward4x4"/> stays bit-exact against <see cref="LibaomReferenceWht.Fwht4x4"/>
    /// at these extremes (the case most likely to expose an intermediate-overflow bug the mid-range CoeffCheck
    /// data wouldn't reach) and that every output coefficient stays within libaom's own real magnitude bound
    /// (<c>row_length * kDctMaxValue &lt;&lt; (bit_depth - 8)</c>, <c>row_length = 4</c> for a 16-coefficient
    /// 4x4 block, <c>kDctMaxValue = 16384</c> -- 65536 at 8-bit).
    /// </summary>
    [Theory]
    [InlineData(255)]
    [InlineData(-255)]
    public void MemCheck_ExtremeValues_MatchReferenceAndStayInBound(int extreme)
    {
        var input = new short[16];
        Array.Fill(input, (short)extreme);

        var expected = new int[16];
        LibaomReferenceWht.Fwht4x4(input, expected, stride: 4);

        var residual = new int[16];
        for (int j = 0; j < 16; j++)
        {
            residual[j] = input[j];
        }

        var actual = new int[16];
        Av1ForwardWht.Forward4x4(residual, actual);

        Assert.Equal(expected, actual);

        const int bound = 4 * 16384; // row_length (4) * kDctMaxValue, bit_depth == 8 so no left-shift.
        Assert.All(actual, value => Assert.True(Math.Abs(value) <= bound, $"coefficient {value} exceeds libaom's own real magnitude bound {bound}"));
    }

    /// <summary>
    /// Extreme-value variant of <see cref="MemCheck_ExtremeValues_MatchReferenceAndStayInBound"/> using
    /// libaom's own random +/-mask_ pattern (<c>RunMemCheck</c>'s own per-coefficient
    /// <c>rnd.Rand8() % 2 ? mask_ : -mask_</c>), rather than every coefficient pinned to the same sign --
    /// covers the mixed-sign extreme case the two hand-picked all-same-sign cases above don't.
    /// </summary>
    [Fact]
    public void MemCheck_RandomSignedExtremes_MatchReferenceAndStayInBound()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        const int countTestBlock = 5000;
        const int mask = 255;
        const int bound = 4 * 16384;

        for (int i = 0; i < countTestBlock; i++)
        {
            var input = new short[16];
            for (int j = 0; j < 16; j++)
            {
                input[j] = (short)(rnd.Rand8() % 2 != 0 ? mask : -mask);
            }

            var expected = new int[16];
            LibaomReferenceWht.Fwht4x4(input, expected, stride: 4);

            var residual = new int[16];
            for (int j = 0; j < 16; j++)
            {
                residual[j] = input[j];
            }

            var actual = new int[16];
            Av1ForwardWht.Forward4x4(residual, actual);

            Assert.Equal(expected, actual);
            Assert.All(actual, value => Assert.True(Math.Abs(value) <= bound, $"coefficient {value} exceeds libaom's own real magnitude bound {bound}"));
        }
    }

    private static void AssertRoundTripsExactly(int[] residual)
    {
        int[] coeff = new int[16];
        Av1ForwardWht.Forward4x4(residual, coeff);

        int[] dequant = new int[64 * 64];
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                dequant[(i * 64) + j] = coeff[(i * 4) + j];
            }
        }

        int[] reconstructed = new int[16];

        // planeTxType is ignored by Inverse2D whenever lossless is true (it branches to InverseWht
        // unconditionally), so DctDct here is just a placeholder value, not a meaningful choice.
        Av1InverseTransform.Inverse2D(dequant, reconstructed, Av1TxSize.Tx4x4, Av1TxType.DctDct, lossless: true, bitDepth: 8);

        Assert.Equal(residual, reconstructed);
    }
}
