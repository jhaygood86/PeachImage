using PeachImage.Formats.Webp;
using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Pins <see cref="Vp8BoolDecoder"/> against RFC 6386's reference range decoder, bit for bit.
/// </summary>
/// <remarks>
/// The production decoder follows libwebp's form instead: a count-leading-zeros renormalization shift rather
/// than a doubling loop, and a 56-bit bulk refill rather than a byte at a time. Those are equivalent for
/// reasons that have to be argued rather than read off the code (see <see cref="Vp8BoolDecoder.GetBit"/>'s
/// remarks), and this decoder sits underneath every single bit of every lossy WebP file — so the equivalence
/// is checked directly against a transliteration of the reference algorithm held here in the test project.
/// </remarks>
public class Vp8BoolDecoderDifferentialTests
{
    /// <summary>
    /// 500 random buffers of up to 4 KB, 20,000 reads each at random probabilities, every bit compared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one constraint is that the first byte is not <c>0xFF</c>, and it is load-bearing rather than
    /// cosmetic. A range coder maintains <c>value &lt; range &lt;&lt; 8</c>. The reference decoder primes
    /// <c>value</c> with two raw bytes against an initial range of 255, so a leading <c>0xFF</c> — and only a
    /// leading <c>0xFF</c> — can start it above <c>255 &lt;&lt; 8</c>. Once that invariant is broken the
    /// reference's <c>value</c> grows without bound under renormalization (it was observed reaching 2^30), and
    /// its comparison then consults bits above the top eight. The production decoder cannot follow it there: it
    /// reads exactly an 8-bit window by construction, which is precisely what makes the bulk refill possible.
    /// </para>
    /// <para>
    /// So the two agree on every input a range coder can actually produce, and disagree only on byte sequences
    /// that are not valid VP8 bitstreams, where neither answer is meaningful. That is not a theoretical
    /// argument: with this constraint the fuzz runs clean across all 500 seeds, and without it the first
    /// divergence appears at seed 93, step 36. The guarantee for real files comes from
    /// <c>WebpDecodeHashTests</c>, which confirms all 131 corpus files and 6 benchmark assets — including the
    /// <c>vp80-*</c> conformance series and <c>lossy_extreme_probabilities.webp</c> — decode to identical
    /// pixels across this rewrite.
    /// </para>
    /// </remarks>
    [Fact]
    public void GetBit_AgreesWithTheReferenceDecoder_OverRandomBuffersAndProbabilities()
    {
        for (int seed = 0; seed < 500; seed++)
        {
            var random = new Random(seed);
            byte[] data = new byte[random.Next(1, 4097)];
            random.NextBytes(data);

            if (data[0] == 0xFF)
            {
                data[0] = 0xFE;
            }

            var actual = new Vp8BoolDecoder(data, 0, data.Length);
            var expected = new ReferenceBoolDecoder(data, 0, data.Length);

            for (int step = 0; step < 20_000; step++)
            {
                int probability = random.Next(1, 256);
                Assert.Equal(expected.GetBit(probability), actual.GetBit(probability));
            }
        }
    }

    /// <summary>
    /// Buffer lengths straddling the 8-byte bulk-refill window. The bulk path needs eight bytes available, so
    /// these are exactly the lengths where a decode crosses from it into the byte-at-a-time tail, and then into
    /// the synthesized zero padding past the end.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void GetBit_AgreesWithTheReferenceDecoder_AtEveryBufferLengthAroundTheRefillWindow(int length)
    {
        foreach (byte fill in new byte[] { 0x00, 0xFF, 0x5A })
        {
            byte[] data = new byte[length];
            Array.Fill(data, fill);

            var actual = new Vp8BoolDecoder(data, 0, length);
            var expected = new ReferenceBoolDecoder(data, 0, length);

            // Far more reads than the buffer can supply, so most of this runs on synthesized zero padding.
            for (int step = 0; step < 2_000; step++)
            {
                int probability = 1 + (step % 255);
                Assert.Equal(expected.GetBit(probability), actual.GetBit(probability));
            }
        }
    }

    /// <summary>
    /// A decoder over a slice of a larger array must never read outside its own range. If the bulk refill were
    /// bounded by the array rather than by the slice it would happily decode the following partition's bytes —
    /// producing wrong bits rather than faulting, which is the failure mode that would be hardest to spot.
    /// </summary>
    [Fact]
    public void GetBit_OverASliceOfALargerBuffer_NeverReadsBeyondItsOwnRange()
    {
        var random = new Random(8675309);
        byte[] backing = new byte[512];
        random.NextBytes(backing);

        foreach (int start in new[] { 0, 1, 7, 64, 300 })
        {
            foreach (int length in new[] { 1, 3, 8, 9, 40 })
            {
                byte[] isolated = backing.AsSpan(start, length).ToArray();

                var actual = new Vp8BoolDecoder(backing, start, length);
                var expected = new ReferenceBoolDecoder(isolated, 0, length);

                for (int step = 0; step < 1_000; step++)
                {
                    int probability = 1 + (step % 255);
                    Assert.Equal(expected.GetBit(probability), actual.GetBit(probability));
                }
            }
        }
    }

    [Fact]
    public void Constructor_RangeOutsideTheBuffer_Throws()
    {
        byte[] data = new byte[4];

        Assert.Throws<WebpDecodingException>(() => new Vp8BoolDecoder(data, 0, 5));
        Assert.Throws<WebpDecodingException>(() => new Vp8BoolDecoder(data, 3, 2));
        Assert.Throws<WebpDecodingException>(() => new Vp8BoolDecoder(data, -1, 2));
        Assert.Throws<WebpDecodingException>(() => new Vp8BoolDecoder(data, 0, -1));
    }

    /// <summary>
    /// RFC 6386 section 7.3's decoder, transliterated directly from the specification's pseudocode: an unbiased
    /// range, a 16-bit value window, and renormalization by doubling one bit at a time with a byte pulled in
    /// every eighth shift. Deliberately the slow, literal shape — it is the oracle, not an implementation.
    /// </summary>
    private sealed class ReferenceBoolDecoder
    {
        private readonly byte[] _buffer;
        private readonly int _start;
        private readonly int _length;

        private int _pos;
        private uint _range;
        private uint _value;
        private int _bitCount;

        public ReferenceBoolDecoder(byte[] buffer, int start, int length)
        {
            _buffer = buffer;
            _start = start;
            _length = length;
            _pos = 0;
            _range = 255;
            _bitCount = 0;
            _value = ((uint)NextByte() << 8) | NextByte();
        }

        public int GetBit(int probability)
        {
            uint split = 1u + (((_range - 1u) * (uint)probability) >> 8);
            uint bigSplit = split << 8;

            int bit;
            if (_value >= bigSplit)
            {
                bit = 1;
                _range -= split;
                _value -= bigSplit;
            }
            else
            {
                bit = 0;
                _range = split;
            }

            while (_range < 128)
            {
                _value <<= 1;
                _range <<= 1;
                if (++_bitCount == 8)
                {
                    _bitCount = 0;
                    _value |= NextByte();
                }
            }

            return bit;
        }

        private byte NextByte() => (uint)_pos < (uint)_length ? _buffer[_start + _pos++] : (byte)0;
    }
}
