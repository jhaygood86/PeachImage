using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Port of libaom's own <c>test/bitwriter_buffer_test.cc</c> (<c>BitwriterBufferTest.UvlcOneByte</c> and
/// <c>BitwriterBufferTest.Uvlc31LeadingZeros</c>) plus broader coverage of the same real primitives that
/// file's C++ suite leaves untested. Maps onto this project's <see cref="Av1BitWriter"/> /
/// <see cref="Av1BitReader"/> -- the write/read pair for AV1's <em>uncompressed</em> header syntax (spec
/// §4.10: <c>f(n)</c>, <c>uvlc()</c>, <c>su(n)</c>, <c>ns(n)</c>) -- as follows:
/// <list type="bullet">
/// <item><c>aom_wb_write_bit</c>/<c>aom_wb_write_literal</c>/<c>aom_wb_write_unsigned_literal</c>/<c>aom_rb_read_literal</c> -&gt; <see cref="Av1BitWriter.WriteBits"/>/<see cref="Av1BitReader.ReadBits"/>.</item>
/// <item><c>aom_wb_write_uvlc</c>/<c>aom_rb_read_uvlc</c> -&gt; <see cref="Av1BitWriter.WriteUvlc"/>/<see cref="Av1BitReader.ReadUvlc"/>.</item>
/// <item>AV1 spec §4.10.6 <c>su(n)</c> -&gt; <see cref="Av1BitWriter.WriteSu"/>/<see cref="Av1BitReader.ReadSu"/>.</item>
/// <item><c>wb_write_primitive_quniform</c>/<c>aom_rb_read_primitive_quniform</c> (spec's <c>ns(n)</c>) -&gt; <see cref="Av1BitWriter.WriteNs"/>/<see cref="Av1BitReader.ReadNs"/>.</item>
/// </list>
///
/// <para>Unlike libaom's own SIMD-vs-C-reference test structure, PeachImage's writer and reader are already a
/// matched pair with no separate reference to diff against, so a plain round-trip test alone could hide a bug
/// present identically on both sides. This file instead cross-checks against
/// <see cref="LibaomReferenceBitWriter"/>/<see cref="LibaomReferenceBitReader"/> -- a second, independent
/// transcription of libaom's own real C source (see that file's remarks) -- in both directions: the reference
/// writer's bytes read back correctly by <see cref="Av1BitReader"/>, and <see cref="Av1BitWriter"/>'s bytes
/// read back correctly by the reference reader. libaom's own test file has no randomized coverage at all for
/// any of these primitives (both its tests are small fixed tables), so the random-vector iteration counts and
/// ranges below are this port's own choice, not transcribed from libaom -- sized loosely after the
/// <c>kNumIterations</c>-style counts used by libaom's own randomized tests elsewhere in this codebase's other
/// ports.</para>
/// </summary>
public class Av1BitWriterReaderTests
{
    private const int RandomIterations = 3000;

    // === 1. Direct port of BitwriterBufferTest.UvlcOneByte ================================================

    /// <summary>
    /// Table 25 in ITU-T H.274 (V3) (09/2023) Exp-Golomb codes for codeNum 0..14, exactly as libaom's own
    /// test hardcodes them: { expected total bit_offset, expected first output byte }.
    /// </summary>
    public static IEnumerable<object[]> UvlcOneByteCases()
    {
        (uint bitOffset, byte firstByte)[] expected =
        [
            (1, 0x80), // 0
            (3, 0x40), // 1
            (3, 0x60), // 2
            (5, 0x20), // 3
            (5, 0x28), // 4
            (5, 0x30), // 5
            (5, 0x38), // 6
            (7, 0x10), // 7
            (7, 0x12), // 8
            (7, 0x14), // 9
            (7, 0x16), // 10
            (7, 0x18), // 11
            (7, 0x1a), // 12
            (7, 0x1c), // 13
            (7, 0x1e), // 14
        ];

        for (int i = 0; i < expected.Length; i++)
        {
            yield return [(uint)i, expected[i].bitOffset, expected[i].firstByte];
        }
    }

    [Theory]
    [MemberData(nameof(UvlcOneByteCases))]
    public void WriteUvlc_MatchesLibaomTestTable_UvlcOneByte(uint value, uint expectedBitOffset, byte expectedFirstByte)
    {
        var writer = new Av1BitWriter();
        writer.WriteUvlc(value);

        Assert.Equal((int)expectedBitOffset, writer.BitsWritten);
        Assert.Equal(expectedFirstByte, writer.ToArray()[0]);
    }

    // === 2. Direct port of BitwriterBufferTest.Uvlc31LeadingZeros =========================================

    [Fact]
    public void WriteUvlc_MatchesLibaomTestTable_Uvlc31LeadingZeros_TwoToThe31MinusOne()
    {
        var writer = new Av1BitWriter();
        writer.WriteUvlc(0x7fffffff);

        Assert.Equal(63, writer.BitsWritten);
        byte[] bytes = writer.ToArray();
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 }, bytes);
    }

    [Fact]
    public void WriteUvlc_MatchesLibaomTestTable_Uvlc31LeadingZeros_TwoToThe32MinusTwo()
    {
        var writer = new Av1BitWriter();
        writer.WriteUvlc(0xfffffffe);

        Assert.Equal(63, writer.BitsWritten);
        byte[] bytes = writer.ToArray();
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01, 0xff, 0xff, 0xff, 0xfe }, bytes);
    }

    // === 3. f(n) literal: random cross-check against the independent reference ============================

    [Fact]
    public void WriteBits_MatchesReferenceLiteralFormula_RandomValuesAndWidths()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);

        for (int iter = 0; iter < RandomIterations; iter++)
        {
            int n = 1 + rnd.PseudoUniform(32); // 1..32 bits, matching aom_wb_write_unsigned_literal's own "bits <= 32".
            uint raw = rnd.Rand31();
            uint value = n == 32 ? raw : raw & ((1u << n) - 1);

            var writer = new Av1BitWriter();
            writer.WriteBits(value, n);

            var reference = new LibaomReferenceBitWriter();
            reference.WriteUnsignedLiteral(value, n);

            Assert.Equal(reference.BitOffset, writer.BitsWritten);
            Assert.Equal(reference.ToByteArray(), writer.ToArray());

            var reader = new Av1BitReader(writer.ToArray(), 0, writer.ToArray().Length);
            Assert.Equal(value, reader.ReadBits(n));
        }
    }

    // === 4. uvlc(): random cross-check against the independent reference formula ==========================

    [Fact]
    public void WriteUvlc_MatchesReferenceFormula_RandomValues()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);

        for (int iter = 0; iter < RandomIterations; iter++)
        {
            // Mix small values (where uvlc is interesting) with the full 32-bit range, but never
            // UINT32_MAX - 1's successor (0xFFFFFFFF): aom_wb_write_uvlc's own header comment documents
            // that as an invalid input (v = value + 1 would wrap to 0).
            uint value = (iter % 3 == 0) ? (uint)rnd.PseudoUniform(64) : (rnd.Rand31() << 1) | (uint)rnd.PseudoUniform(2);
            if (value == 0xffffffff)
            {
                value--;
            }

            var writer = new Av1BitWriter();
            writer.WriteUvlc(value);

            var reference = new LibaomReferenceBitWriter();
            reference.WriteUvlc(value);

            Assert.Equal(reference.BitOffset, writer.BitsWritten);
            Assert.Equal(reference.ToByteArray(), writer.ToArray());

            var reader = new Av1BitReader(writer.ToArray(), 0, writer.ToArray().Length);
            Assert.Equal(value, reader.ReadUvlc());
        }
    }

    /// <summary>Cross-direction: the independent reference writer's bytes, read back by the real <see cref="Av1BitReader"/>.</summary>
    [Fact]
    public void ReadUvlc_ReadsBytesFromReferenceWriter_RandomValues()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed + 1);

        for (int iter = 0; iter < RandomIterations; iter++)
        {
            uint value = (uint)rnd.PseudoUniform(int.MaxValue);

            var reference = new LibaomReferenceBitWriter();
            reference.WriteUvlc(value);
            byte[] bytes = reference.ToByteArray();

            var reader = new Av1BitReader(bytes, 0, bytes.Length);
            Assert.Equal(value, reader.ReadUvlc());
            Assert.Equal(reference.BitOffset, reader.BitsRead);
        }
    }

    /// <summary>Cross-direction: the real <see cref="Av1BitWriter"/>'s bytes, read back by the independent reference reader.</summary>
    [Fact]
    public void ReferenceReader_ReadsBytesFromAv1BitWriter_RandomValues()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed + 2);

        for (int iter = 0; iter < RandomIterations; iter++)
        {
            uint value = (uint)rnd.PseudoUniform(int.MaxValue);

            var writer = new Av1BitWriter();
            writer.WriteUvlc(value);
            byte[] bytes = writer.ToArray();

            var reference = new LibaomReferenceBitReader(bytes);
            Assert.Equal(value, reference.ReadUvlc());
            Assert.Equal(writer.BitsWritten, reference.BitOffset);
        }
    }

    // === 5. su(n): hand-computed table (worked by hand from the AV1 spec, independent of any code) =========

    public static IEnumerable<object[]> SuHandComputedCases()
    {
        // n = 1: signMask = 1, range [-1, 0].
        yield return [0, 1, new[] { false }];
        yield return [-1, 1, new[] { true }];

        // n = 3: signMask = 4, range [-4, 3]. Bits listed MSB first.
        yield return [0, 3, new[] { false, false, false }];
        yield return [1, 3, new[] { false, false, true }];
        yield return [3, 3, new[] { false, true, true }];
        yield return [-1, 3, new[] { true, true, true }];
        yield return [-2, 3, new[] { true, true, false }];
        yield return [-4, 3, new[] { true, false, false }];
    }

    [Theory]
    [MemberData(nameof(SuHandComputedCases))]
    public void WriteSu_MatchesHandComputedSpecTable(int value, int n, bool[] expectedBitsMsbFirst)
    {
        var writer = new Av1BitWriter();
        writer.WriteSu(value, n);

        Assert.Equal(n, writer.BitsWritten);
        byte[] bytes = writer.ToArray();
        for (int i = 0; i < n; i++)
        {
            bool actualBit = ((bytes[i >> 3] >> (7 - (i & 7))) & 1) != 0;
            Assert.True(expectedBitsMsbFirst[i] == actualBit, $"bit {i} of su({n}) for value {value}");
        }

        var reader = new Av1BitReader(bytes, 0, bytes.Length);
        Assert.Equal(value, reader.ReadSu(n));
    }

    // === 6. su(n): random cross-check against the independent spec-literal reference ======================

    [Fact]
    public void WriteSu_MatchesReferenceFormula_RandomValuesAndWidths()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed + 3);

        for (int iter = 0; iter < RandomIterations; iter++)
        {
            // n = 32 is excluded: 1 << (n - 1) computed as a 32-bit int overflows to int.MinValue at n = 32,
            // and WriteSu is never called with n anywhere near that large in production code (max real call
            // site is ReadSu(7) in Av1FrameHeader) -- so this deliberately stays within the range the type is
            // actually used for, matching su(n)'s own spec role as a small signed field, not a general 32-bit literal.
            int n = 1 + rnd.PseudoUniform(31);
            int signMask = 1 << (n - 1);
            int value = signMask - 1 - rnd.PseudoUniform(2 * signMask); // uniform over [-signMask, signMask - 1]

            var writer = new Av1BitWriter();
            writer.WriteSu(value, n);

            var reference = new LibaomReferenceBitWriter();
            reference.WriteSuLiteral(value, n);

            Assert.Equal(reference.BitOffset, writer.BitsWritten);
            Assert.Equal(reference.ToByteArray(), writer.ToArray());

            var reader = new Av1BitReader(writer.ToArray(), 0, writer.ToArray().Length);
            Assert.Equal(value, reader.ReadSu(n));

            var referenceReader = new LibaomReferenceBitReader(writer.ToArray());
            Assert.Equal(value, referenceReader.ReadSuLiteral(n));
        }
    }

    // === 7. ns(n): hand-computed table (worked by hand from wb_write_primitive_quniform) ===================

    public static IEnumerable<object[]> NsHandComputedCases()
    {
        // n = 3: l = get_msb(3) + 1 = 2, m = (1 << 2) - 3 = 1.
        yield return [(uint)0, 3u, new[] { false }];
        yield return [(uint)1, 3u, new[] { true, false }];
        yield return [(uint)2, 3u, new[] { true, true }];

        // n = 5: l = get_msb(5) + 1 = 3, m = (1 << 3) - 5 = 3.
        yield return [(uint)0, 5u, new[] { false, false }];
        yield return [(uint)1, 5u, new[] { false, true }];
        yield return [(uint)2, 5u, new[] { true, false }];
        yield return [(uint)3, 5u, new[] { true, true, false }];
        yield return [(uint)4, 5u, new[] { true, true, true }];
    }

    [Theory]
    [MemberData(nameof(NsHandComputedCases))]
    public void WriteNs_MatchesHandComputedTable(uint value, uint n, bool[] expectedBitsMsbFirst)
    {
        var writer = new Av1BitWriter();
        writer.WriteNs(value, n);

        Assert.Equal(expectedBitsMsbFirst.Length, writer.BitsWritten);
        byte[] bytes = writer.ToArray();
        for (int i = 0; i < expectedBitsMsbFirst.Length; i++)
        {
            bool actualBit = ((bytes[i >> 3] >> (7 - (i & 7))) & 1) != 0;
            Assert.True(expectedBitsMsbFirst[i] == actualBit, $"bit {i} of ns({n}) for value {value}");
        }

        var reader = new Av1BitReader(bytes, 0, bytes.Length);
        Assert.Equal(value, reader.ReadNs(n));
    }

    [Fact]
    public void WriteNs_HandlesDegenerateWidths_NoBitsWritten()
    {
        foreach (uint n in new uint[] { 0, 1 })
        {
            var writer = new Av1BitWriter();
            writer.WriteNs(0, n);
            Assert.Equal(0, writer.BitsWritten);

            var reader = new Av1BitReader(writer.ToArray(), 0, 0);
            Assert.Equal(0u, reader.ReadNs(n));
        }
    }

    // === 8. ns(n): random cross-check against the independent reference formula ===========================

    [Fact]
    public void WriteNs_MatchesReferenceFormula_RandomValuesAndWidths()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed + 4);

        for (int iter = 0; iter < RandomIterations; iter++)
        {
            uint n = (uint)(2 + rnd.PseudoUniform(5000));
            uint value = (uint)rnd.PseudoUniform((int)n);

            var writer = new Av1BitWriter();
            writer.WriteNs(value, n);

            var reference = new LibaomReferenceBitWriter();
            reference.WriteQuniform((int)n, (int)value);

            Assert.Equal(reference.BitOffset, writer.BitsWritten);
            Assert.Equal(reference.ToByteArray(), writer.ToArray());

            var reader = new Av1BitReader(writer.ToArray(), 0, writer.ToArray().Length);
            Assert.Equal(value, reader.ReadNs(n));

            var referenceReader = new LibaomReferenceBitReader(writer.ToArray());
            Assert.Equal((int)value, referenceReader.ReadQuniform((int)n));
        }
    }

    // === 9. byte_alignment() / trailing_bits() =============================================================

    [Fact]
    public void ByteAlign_PadsWithZeroBitsToNextByteBoundary()
    {
        for (int n = 1; n <= 8; n++)
        {
            var writer = new Av1BitWriter();
            writer.WriteBits(0x1u, n); // one nonzero bit followed by n - 1 zero bits (all-ones would also work).
            writer.ByteAlign();

            Assert.Equal(0, writer.BitsWritten % 8);
            Assert.Equal(8, writer.BitsWritten); // exactly one byte's worth for n in [1, 8].

            var reader = new Av1BitReader(writer.ToArray(), 0, writer.ToArray().Length);
            Assert.Equal(1u, reader.ReadBits(n));
            reader.ByteAlign();
            Assert.Equal(writer.BitsWritten, reader.BitsRead);
        }
    }

    [Fact]
    public void WriteTrailingBits_WritesStopBitThenZeroPad_WhenNotByteAligned()
    {
        var writer = new Av1BitWriter();
        writer.WriteBits(0b101u, 3);
        writer.WriteTrailingBits();

        Assert.Equal(8, writer.BitsWritten);
        Assert.Equal((byte)0b1011_0000, writer.ToArray()[0]);
    }

    [Fact]
    public void WriteTrailingBits_WritesFullExtraByte_WhenAlreadyByteAligned()
    {
        var writer = new Av1BitWriter();
        writer.WriteBits(0xabu, 8);
        writer.WriteTrailingBits();

        Assert.Equal(16, writer.BitsWritten);
        byte[] bytes = writer.ToArray();
        Assert.Equal((byte)0xab, bytes[0]);
        Assert.Equal((byte)0x80, bytes[1]); // stop bit "1" followed by seven zero padding bits.
    }

    // === 10. Combined randomized round trip: an interleaved sequence of every field kind ==================

    /// <summary>
    /// Simulates a small header-like stream mixing every primitive this file covers, the way real AV1
    /// uncompressed headers interleave <c>f(n)</c>/<c>uvlc()</c>/<c>su(n)</c>/<c>ns(n)</c> fields --
    /// catching any bit-position bookkeeping bug that a single-field-at-a-time test could miss.
    /// </summary>
    [Fact]
    public void RoundTrip_InterleavedFieldSequence_ExactlyReproducesEveryValue()
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed + 5);

        for (int iter = 0; iter < 500; iter++)
        {
            var writer = new Av1BitWriter();
            var plan = new List<(char kind, long value, int width)>();

            int fieldCount = 4 + rnd.PseudoUniform(12);
            for (int f = 0; f < fieldCount; f++)
            {
                int kind = rnd.PseudoUniform(5);
                switch (kind)
                {
                    case 0:
                        {
                            int n = 1 + rnd.PseudoUniform(31);
                            uint value = rnd.Rand31() & ((1u << n) - 1);
                            writer.WriteBits(value, n);
                            plan.Add(('b', value, n));
                            break;
                        }

                    case 1:
                        {
                            uint value = (uint)rnd.PseudoUniform(1 << 20);
                            writer.WriteUvlc(value);
                            plan.Add(('u', value, 0));
                            break;
                        }

                    case 2:
                        {
                            int n = 1 + rnd.PseudoUniform(16);
                            int signMask = 1 << (n - 1);
                            int value = signMask - 1 - rnd.PseudoUniform(2 * signMask);
                            writer.WriteSu(value, n);
                            plan.Add(('s', value, n));
                            break;
                        }

                    case 3:
                        {
                            uint n = (uint)(2 + rnd.PseudoUniform(500));
                            uint value = (uint)rnd.PseudoUniform((int)n);
                            writer.WriteNs(value, n);
                            plan.Add(('e', value, (int)n));
                            break;
                        }

                    default:
                        writer.ByteAlign();
                        plan.Add(('a', 0, 0));
                        break;
                }
            }

            byte[] bytes = writer.ToArray();
            var reader = new Av1BitReader(bytes, 0, bytes.Length);
            foreach ((char kind, long expectedValue, int width) in plan)
            {
                switch (kind)
                {
                    case 'b':
                        Assert.Equal((uint)expectedValue, reader.ReadBits(width));
                        break;
                    case 'u':
                        Assert.Equal((uint)expectedValue, reader.ReadUvlc());
                        break;
                    case 's':
                        Assert.Equal((int)expectedValue, reader.ReadSu(width));
                        break;
                    case 'e':
                        Assert.Equal((uint)expectedValue, reader.ReadNs((uint)width));
                        break;
                    case 'a':
                        reader.ByteAlign();
                        break;
                }
            }

            Assert.Equal(writer.BitsWritten, reader.BitsRead);
        }
    }
}
