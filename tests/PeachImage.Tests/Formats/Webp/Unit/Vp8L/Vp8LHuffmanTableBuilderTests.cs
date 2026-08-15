using PeachImage.Formats.Webp;
using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

/// <summary>
/// Correctness tests for <see cref="Vp8LHuffmanTableBuilder"/> — the highest-risk piece of the VP8L decoder,
/// since every pixel decode goes through a table it builds. Cross-checks the builder's table against an
/// independently computed reference: standard MSB-first canonical Huffman code assignment (by increasing
/// length, then symbol order), each code then bit-reversed to get the LSB-first bit pattern a real VP8L
/// bitstream would contain for that symbol. This exercises none of the builder's own machinery (no
/// GetNextKey/ReplicateValue), so it's a genuine differential check, not a restatement of the same algorithm.
/// </summary>
public class Vp8LHuffmanTableBuilderTests
{
    [Fact]
    public void TwoSymbol_Length1Each_DecodesCorrectlyAcrossAllRootSlots()
    {
        AssertDecodesCorrectly([1, 1], rootBits: 2);
    }

    [Fact]
    public void ThreeSymbol_MixedLengths_FitsEntirelyInRootTable()
    {
        // sym0 len1, sym1/sym2 len2 -- Kraft: 1/2+1/4+1/4=1, exact, all fit within a 2-bit root table.
        AssertDecodesCorrectly([1, 2, 2], rootBits: 2);
    }

    [Fact]
    public void FourSymbol_RequiresSecondLevelTable()
    {
        // sym0 len1, sym1 len2, sym2/sym3 len3 -- Kraft: 1/2+1/4+1/8+1/8=1, exact. rootBits=2 is narrower
        // than the longest code (3), forcing a pointer + second-level table.
        AssertDecodesCorrectly([1, 2, 3, 3], rootBits: 2);
    }

    [Fact]
    public void EightSymbol_AllLength3_ExercisesFullRootTable()
    {
        AssertDecodesCorrectly([3, 3, 3, 3, 3, 3, 3, 3], rootBits: 3);
    }

    [Fact]
    public void SingleSymbol_DecodesWithoutConsumingAnyBits()
    {
        var codeLengths = new int[5];
        codeLengths[2] = 1;

        var table = Vp8LHuffmanTableBuilder.Build(codeLengths, rootBits: 4);

        // An all-zero (and, separately, an all-one) stream must both decode to the sole symbol, and must not
        // consume any bits doing so (the tree has exactly one leaf, at the root, needing zero bits to select).
        byte[] zeros = new byte[4];
        var readerZeros = new Vp8LBitReader(zeros, 0, zeros.Length);
        Assert.Equal(2, table.Decode(readerZeros));
        Assert.Equal(0u, readerZeros.PeekBits(8));

        byte[] ones = [0xFF, 0xFF, 0xFF, 0xFF];
        var readerOnes = new Vp8LBitReader(ones, 0, ones.Length);
        Assert.Equal(2, table.Decode(readerOnes));

        // Confirm no bits were consumed: the reader should still see the original leading byte intact.
        Assert.Equal(0xFFu, readerOnes.PeekBits(8));
    }

    [Fact]
    public void MainAlphabetRootBits_LongCode_RoundTrips()
    {
        // A skewed 6-symbol distribution that pushes codes out to length 5, well past a small root width
        // (forcing a real second-level table exercise). Kraft: 1/2+1/4+1/8+1/16+1/32+1/32 = 1, exact.
        int[] codeLengths = [1, 2, 3, 4, 5, 5];

        // Verify Kraft sums to exactly 1 (sanity on the hand-picked lengths themselves).
        double kraft = codeLengths.Sum(l => Math.Pow(2, -l));
        Assert.Equal(1.0, kraft, precision: 10);

        AssertDecodesCorrectly(codeLengths, rootBits: 2);
        AssertDecodesCorrectly(codeLengths, rootBits: 4);
    }

    [Fact]
    public void InvalidCodeLengths_AllZero_Throws()
    {
        var codeLengths = new int[8];
        Assert.Throws<WebpDecodingException>(() => Vp8LHuffmanTableBuilder.Build(codeLengths, rootBits: 3));
    }

    [Fact]
    public void InvalidCodeLengths_OversubscribedTree_Throws()
    {
        // Three symbols all claiming length 1 -- only two length-1 codes exist (Kraft > 1).
        int[] codeLengths = [1, 1, 1];
        Assert.Throws<WebpDecodingException>(() => Vp8LHuffmanTableBuilder.Build(codeLengths, rootBits: 3));
    }

    [Fact]
    public void InvalidCodeLengths_UndersubscribedTree_Throws()
    {
        // Two symbols of length 2 leave the tree incomplete (Kraft < 1): 1/4+1/4 = 1/2.
        int[] codeLengths = [0, 0, 2, 2];
        Assert.Throws<WebpDecodingException>(() => Vp8LHuffmanTableBuilder.Build(codeLengths, rootBits: 3));
    }

    /// <summary>
    /// Builds a table for <paramref name="codeLengths"/>, computes an independent reference mapping of
    /// symbol -&gt; (LSB-first bit-reversed code, length), then brute-force enumerates every possible
    /// <c>maxLength</c>-bit input window and asserts the table decodes each one to the symbol whose
    /// reference code is a prefix of that window -- covering every root-table slot (and, when codes are
    /// longer than <paramref name="rootBits"/>, every second-level table slot too).
    /// </summary>
    private static void AssertDecodesCorrectly(int[] codeLengths, int rootBits)
    {
        var table = Vp8LHuffmanTableBuilder.Build(codeLengths, rootBits);
        var reference = ComputeReferenceCodes(codeLengths);
        int maxLength = codeLengths.Where(l => l > 0).Max();

        for (uint window = 0; window < (1u << maxLength); window++)
        {
            int expectedSymbol = FindMatch(reference, window, maxLength);

            byte[] bytes = ToLittleEndianBytes(window, maxLength);
            var reader = new Vp8LBitReader(bytes, 0, bytes.Length);
            int actualSymbol = table.Decode(reader);

            Assert.True(
                expectedSymbol == actualSymbol,
                $"window={Convert.ToString(window, 2).PadLeft(maxLength, '0')} expected symbol {expectedSymbol} but got {actualSymbol}.");
        }
    }

    private static int FindMatch(Dictionary<int, (uint Key, int Length)> reference, uint window, int maxLength)
    {
        foreach (var (symbol, (key, length)) in reference)
        {
            uint mask = (1u << length) - 1;
            if ((window & mask) == key)
            {
                return symbol;
            }
        }

        throw new InvalidOperationException($"No reference code matches window {window:B} (this indicates the hand-picked code lengths are not a complete prefix code).");
    }

    private static Dictionary<int, (uint Key, int Length)> ComputeReferenceCodes(int[] codeLengths)
    {
        int maxLength = codeLengths.Where(l => l > 0).DefaultIfEmpty(0).Max();
        var symbolsByLength = new List<int>[maxLength + 1];
        for (int i = 0; i <= maxLength; i++)
        {
            symbolsByLength[i] = [];
        }

        for (int symbol = 0; symbol < codeLengths.Length; symbol++)
        {
            if (codeLengths[symbol] > 0)
            {
                symbolsByLength[codeLengths[symbol]].Add(symbol);
            }
        }

        var result = new Dictionary<int, (uint Key, int Length)>();
        uint code = 0;
        for (int length = 1; length <= maxLength; length++)
        {
            foreach (int symbol in symbolsByLength[length])
            {
                result[symbol] = (ReverseBits(code, length), length);
                code++;
            }

            code <<= 1;
        }

        return result;
    }

    private static uint ReverseBits(uint value, int bitCount)
    {
        uint result = 0;
        for (int i = 0; i < bitCount; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }

    private static byte[] ToLittleEndianBytes(uint window, int bitCount)
    {
        int byteCount = (bitCount + 7) / 8;
        byte[] bytes = new byte[Math.Max(byteCount, 4)];
        for (int i = 0; i < byteCount; i++)
        {
            bytes[i] = (byte)(window >> (i * 8));
        }

        return bytes;
    }
}
