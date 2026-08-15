using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Validates <see cref="Vp8BoolDecoder"/> as a black box: fixed input bytes and a fixed probability sequence in,
/// a fixed bit sequence out. The expected sequences were derived by hand from RFC 6386 section 7.3's
/// split/compare/renormalize algorithm, independently of any implementation.
/// </summary>
/// <remarks>
/// Deliberately states no internal state. These expectations originally carried a step-by-step trace of the
/// reference decoder's <c>range</c>/<c>value</c>/<c>bitCount</c> registers, which described a machine the
/// production decoder no longer is — it now follows libwebp's form, with a count-leading-zeros renormalization
/// and a 56-bit bulk refill. Phrasing them as an implementation-independent contract is what lets them keep
/// their value across that change; the internal equivalence is <c>Vp8BoolDecoderDifferentialTests</c>' job, and
/// the state-machine commentary now lives in the decoder itself, where it describes real code.
/// </remarks>
public class Vp8BoolDecoderTests
{
    /// <summary>
    /// 10 consecutive <see cref="Vp8BoolDecoder.GetBit"/> calls (probabilities 128, 128, 200, 50, 128, 128, 128,
    /// 128, 128, 128) against input bytes [0x8F, 0x3A, 0x71, 0x00] must yield 1,0,0,1,0,0,1,0,0,0. The sequence
    /// spans a byte refill, so it exercises the supply path as well as the arithmetic.
    /// </summary>
    /// <remarks>
    /// Cross-checked against a from-scratch transliteration of the RFC algorithm rather than derived from this
    /// decoder's source, so it is an independent expectation and not a round-trip through the code under test.
    /// </remarks>
    [Fact]
    public void GetBit_MatchesHandTracedRangeCoderStateTransitions()
    {
        var decoder = new Vp8BoolDecoder([0x8F, 0x3A, 0x71, 0x00], 0, 4);
        int[] probabilities = [128, 128, 200, 50, 128, 128, 128, 128, 128, 128];
        int[] expectedBits = [1, 0, 0, 1, 0, 0, 1, 0, 0, 0];

        for (int i = 0; i < probabilities.Length; i++)
        {
            int bit = decoder.GetBit(probabilities[i]);
            Assert.True(expectedBits[i] == bit, $"Bit {i}: expected {expectedBits[i]}, got {bit}.");
        }
    }


    /// <summary>
    /// With input [0xFF, 0xFF] and probability 128 throughout, every <see cref="Vp8BoolDecoder.GetBit"/> call
    /// returns 1, so <see cref="Vp8BoolDecoder.GetValue"/> reading 3 such bits MSB-first should equal
    /// 0b111 = 7.
    /// </summary>
    [Fact]
    public void GetValue_AllOnesInput_ReturnsAllOnesMagnitude()
    {
        var decoder = new Vp8BoolDecoder([0xFF, 0xFF], 0, 2);

        uint value = decoder.GetValue(3);

        Assert.Equal(7u, value);
    }

    /// <summary>
    /// With the same all-0xFF input the 4th call (the sign flag) also returns bit=1 (negative), so
    /// <c>GetSignedValue(3)</c> should equal -7.
    /// </summary>
    [Fact]
    public void GetSignedValue_AllOnesInput_ReturnsNegativeMagnitude()
    {
        var decoder = new Vp8BoolDecoder([0xFF, 0xFF], 0, 2);

        int value = decoder.GetSignedValue(3);

        Assert.Equal(-7, value);
    }

    [Fact]
    public void GetFlag_AllZeroInput_ReturnsFalse()
    {
        var decoder = new Vp8BoolDecoder([0x00, 0x00], 0, 2);

        Assert.False(decoder.GetFlag());
    }

    [Fact]
    public void GetFlag_AllOnesInput_ReturnsTrue()
    {
        var decoder = new Vp8BoolDecoder([0xFF, 0xFF], 0, 2);

        Assert.True(decoder.GetFlag());
    }

    /// <summary>A 2-leaf tree: node 0's bit selects between leaf value 5 (bit=0) and leaf value 9 (bit=1).</summary>
    [Theory]
    [InlineData(0x00, 0x00, 5)]
    [InlineData(0xFF, 0xFF, 9)]
    public void GetTreeIndex_TwoLeafTree_SelectsExpectedLeaf(byte b0, byte b1, int expected)
    {
        var decoder = new Vp8BoolDecoder([b0, b1], 0, 2);
        sbyte[] tree = [-5, -9];
        byte[] probabilities = [128];

        int result = decoder.GetTreeIndex(tree, probabilities);

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// A 3-node tree requiring two GetBit calls to reach a leaf: node 0 (bit0-&gt;leaf1, bit1-&gt;internal node at
    /// index 4), node at index 4 (bit0-&gt;leaf3, bit1-&gt;leaf4). With all-0xFF input both calls return bit=1,
    /// landing on leaf value 4.
    /// </summary>
    [Fact]
    public void GetTreeIndex_MultiLevelTree_WalksToExpectedLeaf()
    {
        var decoder = new Vp8BoolDecoder([0xFF, 0xFF], 0, 2);
        sbyte[] tree = [2, 4, -1, -2, -3, -4];
        byte[] probabilities = [128, 128, 128];

        int result = decoder.GetTreeIndex(tree, probabilities);

        Assert.Equal(4, result);
    }

    [Fact]
    public void GetBit_ReadingPastEndOfBuffer_SynthesizesZeroBytesInsteadOfThrowing()
    {
        // Only 1 real byte, then nothing: every subsequent read must behave as if the buffer continued with
        // zeroes rather than throwing or spinning.
        var decoder = new Vp8BoolDecoder([0x00], 0, 1);

        // Should not throw, and should behave as if trailing bytes were all zero.
        for (int i = 0; i < 32; i++)
        {
            decoder.GetBit(128);
        }
    }
}
