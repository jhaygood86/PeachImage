using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8Encoding;

/// <summary>
/// Validates <see cref="Vp8BoolEncoder"/> by round-tripping through the real, unmodified
/// <see cref="Vp8BoolDecoder"/>: whatever this encoder writes must decode back to exactly what was written. This
/// is the correctness gate every other VP8 lossy encode component depends on, since every later stage (mode
/// writing, coefficient encoding, headers) is only as bit-exact as this one.
/// </summary>
public class Vp8BoolEncoderTests
{
    /// <summary>The exact (probability, bit) sequence <c>Vp8BoolDecoderTests.GetBit_MatchesHandTracedRangeCoderStateTransitions</c> exercises, replayed through the encoder to confirm the same probabilities round-trip regardless of which side produced the bytes.</summary>
    [Fact]
    public void PutBit_HandTracedProbabilitySequence_RoundTrips()
    {
        int[] probabilities = [128, 128, 200, 50, 128, 128, 128, 128, 128, 128];
        int[] bits = [1, 0, 0, 1, 0, 0, 1, 0, 0, 0];

        var encoder = new Vp8BoolEncoder();
        for (int i = 0; i < bits.Length; i++)
        {
            encoder.PutBit(bits[i], probabilities[i]);
        }

        byte[] encoded = encoder.Finish();

        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        for (int i = 0; i < bits.Length; i++)
        {
            int decoded = decoder.GetBit(probabilities[i]);
            Assert.True(bits[i] == decoded, $"Bit {i}: expected {bits[i]}, got {decoded}.");
        }
    }

    [Fact]
    public void PutFlag_AllTrue_RoundTrips()
    {
        var encoder = new Vp8BoolEncoder();
        for (int i = 0; i < 64; i++)
        {
            encoder.PutFlag(true);
        }

        byte[] encoded = encoder.Finish();
        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        for (int i = 0; i < 64; i++)
        {
            Assert.True(decoder.GetFlag());
        }
    }

    [Fact]
    public void PutFlag_AllFalse_RoundTrips()
    {
        var encoder = new Vp8BoolEncoder();
        for (int i = 0; i < 64; i++)
        {
            encoder.PutFlag(false);
        }

        byte[] encoded = encoder.Finish();
        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        for (int i = 0; i < 64; i++)
        {
            Assert.False(decoder.GetFlag());
        }
    }

    [Theory]
    [InlineData(0u, 3)]
    [InlineData(7u, 3)]
    [InlineData(5u, 3)]
    [InlineData(255u, 8)]
    [InlineData(0u, 8)]
    [InlineData(12345u, 16)]
    public void PutValue_RoundTrips(uint value, int numBits)
    {
        var encoder = new Vp8BoolEncoder();
        encoder.PutValue(value, numBits);
        byte[] encoded = encoder.Finish();

        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        uint decoded = decoder.GetValue(numBits);

        Assert.Equal(value, decoded);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(63, 7)]
    [InlineData(-63, 7)]
    [InlineData(1, 4)]
    [InlineData(-1, 4)]
    [InlineData(-15, 4)]
    public void PutSignedValue_RoundTrips(int value, int numBits)
    {
        var encoder = new Vp8BoolEncoder();
        encoder.PutSignedValue(value, numBits);
        byte[] encoded = encoder.Finish();

        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        int decoded = decoder.GetSignedValue(numBits);

        Assert.Equal(value, decoded);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public void PutTreeIndex_TwoLeafTree_RoundTrips(int leaf)
    {
        sbyte[] tree = [-5, -9];
        byte[] probabilities = [128];

        var encoder = new Vp8BoolEncoder();
        encoder.PutTreeIndex(tree, probabilities, leaf);
        byte[] encoded = encoder.Finish();

        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        int decoded = decoder.GetTreeIndex(tree, probabilities);

        Assert.Equal(leaf, decoded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PutTreeIndex_MultiLevelTree_RoundTrips(int leaf)
    {
        sbyte[] tree = [2, 4, -1, -2, -3, -4];
        byte[] probabilities = [128, 128, 128];

        var encoder = new Vp8BoolEncoder();
        encoder.PutTreeIndex(tree, probabilities, leaf);
        byte[] encoded = encoder.Finish();

        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        int decoded = decoder.GetTreeIndex(tree, probabilities);

        Assert.Equal(leaf, decoded);
    }

    /// <summary>Extreme probabilities (1 and 255) push <c>split</c> to the edges of its valid range and force long output runs, which is exactly the condition under which <see cref="Vp8BoolEncoder"/>'s carry-propagation/run-length logic gets exercised.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public void PutBit_ExtremeProbabilityRunOfOnes_RoundTrips(int probability)
    {
        const int count = 200;
        var encoder = new Vp8BoolEncoder();
        for (int i = 0; i < count; i++)
        {
            encoder.PutBit(1, probability);
        }

        byte[] encoded = encoder.Finish();

        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(1, decoder.GetBit(probability));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public void PutBit_ExtremeProbabilityRunOfZeros_RoundTrips(int probability)
    {
        const int count = 200;
        var encoder = new Vp8BoolEncoder();
        for (int i = 0; i < count; i++)
        {
            encoder.PutBit(0, probability);
        }

        byte[] encoded = encoder.Finish();

        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(0, decoder.GetBit(probability));
        }
    }

    /// <summary>A run of alternating extreme-probability bits designed to push accumulated output bytes to exactly 0xFF repeatedly, then resolve with a bit that forces a carry -- the scenario <see cref="Vp8BoolEncoder"/>'s <c>_run</c> counter exists for.</summary>
    [Fact]
    public void PutBit_AlternatingExtremesForcingCarryChains_RoundTrips()
    {
        var bits = new List<int>();
        var probabilities = new List<int>();

        for (int block = 0; block < 30; block++)
        {
            for (int i = 0; i < 8; i++)
            {
                bits.Add(1);
                probabilities.Add(255);
            }

            bits.Add(0);
            probabilities.Add(1);
        }

        var encoder = new Vp8BoolEncoder();
        for (int i = 0; i < bits.Count; i++)
        {
            encoder.PutBit(bits[i], probabilities[i]);
        }

        byte[] encoded = encoder.Finish();

        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        for (int i = 0; i < bits.Count; i++)
        {
            int decoded = decoder.GetBit(probabilities[i]);
            Assert.True(bits[i] == decoded, $"Bit {i}: expected {bits[i]}, got {decoded}.");
        }
    }

    /// <summary>Broad randomized coverage across probabilities and bit values, several fixed seeds for reproducibility.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void PutBit_RandomBitAndProbabilitySequence_RoundTrips(int seed)
    {
        var random = new Random(seed);
        const int count = 2000;
        var bits = new int[count];
        var probabilities = new int[count];

        for (int i = 0; i < count; i++)
        {
            bits[i] = random.Next(2);
            probabilities[i] = random.Next(1, 256);
        }

        var encoder = new Vp8BoolEncoder();
        for (int i = 0; i < count; i++)
        {
            encoder.PutBit(bits[i], probabilities[i]);
        }

        byte[] encoded = encoder.Finish();

        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        for (int i = 0; i < count; i++)
        {
            int decoded = decoder.GetBit(probabilities[i]);
            Assert.True(bits[i] == decoded, $"Bit {i} (seed {seed}): expected {bits[i]}, got {decoded}.");
        }
    }

    [Fact]
    public void Finish_EmptyStream_ProducesDecodableEmptyRead()
    {
        var encoder = new Vp8BoolEncoder();
        byte[] encoded = encoder.Finish();

        // An empty coded stream should still be safely decodable -- reads past the end synthesize zero bytes.
        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        decoder.GetBit(128);
    }

    [Fact]
    public void PutBit_SingleBit_RoundTrips()
    {
        var encoder = new Vp8BoolEncoder();
        encoder.PutBit(1, 1);
        byte[] encoded = encoder.Finish();

        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        Assert.Equal(1, decoder.GetBit(1));
    }
}
