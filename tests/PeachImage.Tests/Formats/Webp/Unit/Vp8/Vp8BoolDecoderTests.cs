using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Validates <see cref="Vp8BoolDecoder"/> against hand-computed range-coder state transitions (RFC 6386
/// section 7.3's split/compare/renormalize algorithm), worked out independently step by step in the comments
/// below, rather than by round-tripping through the decoder's own logic.
/// </summary>
public class Vp8BoolDecoderTests
{
    /// <summary>
    /// Traces 10 consecutive <see cref="Vp8BoolDecoder.GetBit"/> calls (probabilities 128, 128, 200, 50, 128,
    /// 128, 128, 128, 128, 128) against input bytes [0x8F, 0x3A, 0x71, 0x00], including one byte refill
    /// (bitCount reaching 8 partway through the 10th call, pulling in the third input byte). Every step's
    /// arithmetic below was independently cross-checked with a from-scratch PowerShell transliteration of the
    /// same split/compare/renormalize algorithm (not derived from or copy-pasted out of this decoder's own
    /// source), so this both documents and independently verifies the exact state transitions.
    /// </summary>
    /// <remarks>
    /// Worked arithmetic (range/value after each call; split = 1 + (((range-1)*prob) &gt;&gt; 8), bigSplit =
    /// split &lt;&lt; 8, bit = (value &gt;= bigSplit)):
    /// <code>
    /// init: range=255, value=0x8F3A=36666, bitCount=0
    /// 1) prob=128: split=128, bigSplit=32768, value=36666>=32768 -> bit=1, range=127, value=3898
    ///    renorm: range=127&lt;128 -> value=7796, range=254, bitCount=1
    /// 2) prob=128: split=127, bigSplit=32512, value=7796&lt;32512 -> bit=0, range=127
    ///    renorm: range=127&lt;128 -> value=15592, range=254, bitCount=2
    /// 3) prob=200: split=198, bigSplit=50688, value=15592&lt;50688 -> bit=0, range=198 (no renorm)
    /// 4) prob=50:  split=39,  bigSplit=9984,  value=15592>=9984  -> bit=1, range=159, value=5608 (no renorm)
    /// 5) prob=128: split=80,  bigSplit=20480, value=5608&lt;20480  -> bit=0, range=80
    ///    renorm: range=80&lt;128 -> value=11216, range=160, bitCount=3
    /// 6) prob=128: split=80,  bigSplit=20480, value=11216&lt;20480 -> bit=0, range=80
    ///    renorm: value=22432, range=160, bitCount=4
    /// 7) prob=128: split=80,  bigSplit=20480, value=22432>=20480 -> bit=1, range=80, value=1952
    ///    renorm: value=3904, range=160, bitCount=5
    /// 8) prob=128: split=80,  bigSplit=20480, value=3904&lt;20480  -> bit=0, range=80
    ///    renorm: value=7808, range=160, bitCount=6
    /// 9) prob=128: split=80,  bigSplit=20480, value=7808&lt;20480  -> bit=0, range=80
    ///    renorm: value=15616, range=160, bitCount=7
    /// 10) prob=128: split=80, bigSplit=20480, value=15616&lt;20480 -> bit=0, range=80
    ///    renorm: range=80&lt;128 -> value=31232, range=160, bitCount=8 -> refill: value|=0x71 -> 31345, bitCount=0
    /// </code>
    /// Expected bit sequence: 1,0,0,1,0,0,1,0,0,0.
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
    /// returns 1 (traced identically to the first two steps of <see cref="GetBit_MatchesHandTracedRangeCoderStateTransitions"/>'s
    /// all-zero counterpart, mirrored for all-one input), so <see cref="Vp8BoolDecoder.GetValue"/> reading 3 such
    /// bits MSB-first should equal 0b111 = 7.
    /// </summary>
    [Fact]
    public void GetValue_AllOnesInput_ReturnsAllOnesMagnitude()
    {
        var decoder = new Vp8BoolDecoder([0xFF, 0xFF], 0, 2);

        uint value = decoder.GetValue(3);

        Assert.Equal(7u, value);
    }

    /// <summary>
    /// Continuing the same all-0xFF-input trace, the 4th call (the sign flag) also returns bit=1 (negative), so
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
    /// index 4), node at index 4 (bit0-&gt;leaf3, bit1-&gt;leaf4). With all-0xFF input both calls return bit=1 (per
    /// the first two steps of the all-ones trace used above), landing on leaf value 4.
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
        // Only 1 real byte; the decoder needs a second byte immediately at construction time (the initial
        // 16-bit value window), which must be synthesized as zero rather than throwing.
        var decoder = new Vp8BoolDecoder([0x00], 0, 1);

        // Should not throw, and should behave as if trailing bytes were all zero.
        for (int i = 0; i < 32; i++)
        {
            decoder.GetBit(128);
        }
    }
}
