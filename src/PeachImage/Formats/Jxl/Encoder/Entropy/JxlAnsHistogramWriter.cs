using System.Numerics;
using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Entropy;

namespace PeachImage.Formats.Jxl.Encoder.Entropy;

/// <summary>
/// Normalizes symbol counts to the 4096-entry ANS table and serializes them, the inverse of the histogram reader
/// in <see cref="JxlEntropyCode"/>. One or two distinct symbols use the compact "simple" form; everything else
/// uses the general form with run-length-coded log counts and exact (full-precision) mantissas.
/// </summary>
internal static class JxlAnsHistogramWriter
{
    private const int Total = JxlAliasTable.TableSize;
    private const int LogTotal = JxlAliasTable.LogTableSize;

    // Per log-count symbol (0..13): the number of prefix bits and their LSB-first pattern, derived from the decoder's lookup table.
    private static readonly (int Bits, uint Pattern)[] LogCountCodes = BuildLogCountCodes();

    /// <summary>
    /// Scales <paramref name="counts"/> so they sum to <see cref="JxlAliasTable.TableSize"/>, keeping every
    /// observed symbol at 1 or more. An all-zero histogram becomes a single-symbol one.
    /// </summary>
    public static int[] Normalize(ReadOnlySpan<long> counts)
    {
        var result = new int[counts.Length];
        long sum = 0;
        int distinct = 0;
        int largest = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            sum += counts[i];
            if (counts[i] > 0)
            {
                distinct++;
                if (counts[i] > counts[largest])
                {
                    largest = i;
                }
            }
        }

        if (sum == 0)
        {
            if (result.Length == 0)
            {
                result = new int[1];
            }

            result[0] = Total;
            return result;
        }

        if (distinct > Total)
        {
            throw new JxlEncodingException("Too many distinct symbols for the ANS table.");
        }

        long assigned = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                result[i] = (int)Math.Max(1, (counts[i] * Total + (sum >> 1)) / sum);
                assigned += result[i];
            }
        }

        // Absorb the rounding error in the largest bucket, then, if it would drop below 1, shave the next largest ones.
        long diff = Total - assigned;
        while (diff != 0)
        {
            if (diff > 0)
            {
                result[largest] += (int)diff;
                diff = 0;
                continue;
            }

            long room = result[largest] - 1;
            long take = Math.Min(room, -diff);
            result[largest] -= (int)take;
            diff += take;
            if (diff != 0)
            {
                largest = 0;
                for (int i = 1; i < result.Length; i++)
                {
                    if (result[i] > result[largest])
                    {
                        largest = i;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Writes a normalized histogram (summing to 4096).</summary>
    public static void Write(JxlBitWriter writer, ReadOnlySpan<int> counts)
    {
        int distinct = 0;
        int last = -1;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                distinct++;
                last = i;
            }
        }

        if (distinct == 0)
        {
            throw new JxlEncodingException("Cannot write an empty histogram.");
        }

        if (distinct <= 2)
        {
            if (last > 255)
            {
                throw new JxlEncodingException("The ANS alphabet is too large.");
            }

            WriteSimple(writer, counts);
            return;
        }

        writer.WriteBool(false); // not simple
        writer.WriteBool(false); // not flat
        WriteGeneral(writer, counts[..(last + 1)]);
    }

    private static void WriteSimple(JxlBitWriter writer, ReadOnlySpan<int> counts)
    {
        writer.WriteBool(true);
        int first = -1;
        int second = -1;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                if (first < 0)
                {
                    first = i;
                }
                else
                {
                    second = i;
                }
            }
        }

        if (second < 0)
        {
            writer.WriteBits(0, 1);
            WriteVarLenUint8(writer, first);
            return;
        }

        writer.WriteBits(1, 1);
        WriteVarLenUint8(writer, first);
        WriteVarLenUint8(writer, second);
        writer.WriteBits((uint)counts[first], LogTotal);
    }

    private static void WriteGeneral(JxlBitWriter writer, ReadOnlySpan<int> counts)
    {
        int length = counts.Length;
        if (length < 3 || length > 258)
        {
            throw new JxlEncodingException("Unsupported ANS histogram length.");
        }

        // Precision shift 12 (shift + 1 = 13): unary prefix of three 1s, then the low three bits of 13.
        writer.WriteBits(0b111, 3);
        writer.WriteBits(13 & 7, 3);
        WriteVarLenUint8(writer, length - 3);

        Span<int> logs = length <= 512 ? stackalloc int[length] : new int[length];
        int omitPos = 0;
        for (int i = 0; i < length; i++)
        {
            logs[i] = counts[i] > 0 ? 31 - BitOperations.LeadingZeroCount((uint)counts[i]) : -1;
            if (logs[i] > logs[omitPos])
            {
                omitPos = i;
            }
        }

        // Log-count symbols with run-length coding of repeats of the previous count. The omitted position holds
        // the largest log count, and a run must not start right after it (the decoder has not yet derived its count).
        Span<bool> direct = length <= 512 ? stackalloc bool[length] : new bool[length];
        for (int i = 0; i < length;)
        {
            int previous = i > 0 ? counts[i - 1] : 0;
            int run = 0;
            if (i != omitPos + 1)
            {
                while (i + run < length && counts[i + run] == previous)
                {
                    run++;
                }
            }

            if (run >= 4)
            {
                run = Math.Min(run, 259);
                var (bits, pattern) = LogCountCodes[LogTotal + 1];
                writer.WriteBits(pattern, bits);
                WriteVarLenUint8(writer, run - 4);
                i += run;
                continue;
            }

            var (b, p) = LogCountCodes[logs[i] + 1];
            writer.WriteBits(p, b);
            direct[i] = true;
            i++;
        }

        for (int i = 0; i < length; i++)
        {
            int code = logs[i];
            if (!direct[i] || i == omitPos || code <= 0)
            {
                continue;
            }

            // Full precision: the mantissa holds every bit below the leading one.
            writer.WriteBits((uint)(counts[i] - (1 << code)), code);
        }
    }

    // A number in [0, 255] using 1 to 11 bits.
    private static void WriteVarLenUint8(JxlBitWriter writer, int value)
    {
        if (value == 0)
        {
            writer.WriteBool(false);
            return;
        }

        writer.WriteBool(true);
        int nbits = 31 - BitOperations.LeadingZeroCount((uint)value);
        writer.WriteBits((uint)nbits, 3);
        writer.WriteBits((uint)(value - (1 << nbits)), nbits);
    }

    private static (int Bits, uint Pattern)[] BuildLogCountCodes()
    {
        var codes = new (int Bits, uint Pattern)[14];
        var found = new bool[14];
        byte[] table = JxlEntropyCode.LogCountTable;
        for (int index = 0; index < 128; index++)
        {
            int bits = table[index * 2];
            int symbol = table[(index * 2) + 1];
            if (!found[symbol])
            {
                found[symbol] = true;
                codes[symbol] = (bits, (uint)index & ((1u << bits) - 1));
            }
        }

        return codes;
    }
}
