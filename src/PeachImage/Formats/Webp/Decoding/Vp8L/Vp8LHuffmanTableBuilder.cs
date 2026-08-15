using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Builds a canonical Huffman decoding table from an array of per-symbol code lengths, one length per
/// symbol index (0 meaning "symbol unused"). A faithful transliteration of libwebp's
/// <c>BuildHuffmanTable</c>/<c>GetNextKey</c>/<c>ReplicateValue</c>/<c>NextTableBitSize</c> (<c>src/utils/huffman_utils.c</c>),
/// which is what makes this correctness-critical: every VP8L pixel goes through a table built here.
/// </summary>
/// <remarks>
/// <para>
/// Canonical Huffman codes are assigned in order of increasing length, and within a length in symbol order —
/// the encoder never transmits the codes themselves, only each symbol's code *length*, and both sides derive
/// the same codes deterministically from that. What makes VP8L's decode side fast is that the code's bits are
/// consumed least-significant-bit-first, so a decoding table can be indexed directly by <c>PeekBits(root_bits)</c>
/// with no bit-reversal at lookup time — <em>if</em> the table was built by walking prefixes in the matching
/// bit-reversed order. <see cref="GetNextKey"/> is exactly that: given the current bit-reversed prefix
/// ("key") and a code length, it produces the next key in the sequence a real LSB-first bitstream would
/// produce, without ever materializing the underlying MSB-first canonical code and reversing it.
/// </para>
/// <para>
/// The table has two levels: symbols whose code fits within <see cref="LengthsRootBits"/>/<see cref="MainRootBits"/>
/// bits live directly in the root table (each replicated across every slot sharing its low-bits prefix, via
/// <see cref="ReplicateValue"/>, so a root lookup alone resolves them). Longer codes share a root slot that
/// instead points at a dedicated second-level table sized to just that prefix's longest code
/// (<see cref="NextTableBitSize"/>) — keeping the whole structure compact instead of building one giant
/// <c>1 &lt;&lt; 15</c>-entry table for a tree that might only need a handful of long codes.
/// </para>
/// <para>
/// <see cref="Build"/> runs the construction twice, exactly as libwebp's <c>VP8LBuildHuffmanTable</c> wrapper
/// does: once to compute the total flat-array size the tree needs (root plus every second-level table), then
/// again to actually allocate and fill it. Both passes re-derive the length histogram from scratch and are
/// otherwise identical, so they're guaranteed to agree on size.
/// </para>
/// </remarks>
internal static class Vp8LHuffmanTableBuilder
{
    /// <summary>Root table width used for VP8L's five per-group alphabet trees (green/red/blue/alpha/distance).</summary>
    public const int MainRootBits = 8;

    /// <summary>Root table width used for the small, 19-symbol "code length code" tree (see <c>Vp8LCodeLengthReader</c>).</summary>
    public const int LengthsRootBits = 7;

    private const int MaxAllowedCodeLength = WebpDecodingLimits.MaxHuffmanCodeLength;

    /// <summary>
    /// Builds a table for <paramref name="codeLengths"/> (one entry per symbol; 0 = symbol unused). Throws
    /// <see cref="WebpDecodingException"/> if the lengths don't form a valid canonical Huffman tree (Kraft
    /// inequality violated, over-subscribed table, or every length is zero).
    /// </summary>
    public static Vp8LHuffmanTable Build(ReadOnlySpan<int> codeLengths, int rootBits)
    {
        int totalSize = BuildInternal(codeLengths, rootBits, null);
        if (totalSize <= 0)
        {
            throw new WebpDecodingException("Invalid VP8L Huffman code: code lengths do not form a valid canonical Huffman tree.");
        }

        var entries = new Vp8LHuffmanCode[totalSize];
        BuildInternal(codeLengths, rootBits, entries);

        return new Vp8LHuffmanTable { Entries = entries, RootBits = rootBits };
    }

    /// <summary>
    /// Runs the construction algorithm once. When <paramref name="entries"/> is <see langword="null"/>, this
    /// only computes and returns the required total table size (no writes) — mirroring libwebp's
    /// null-<c>root_table</c> sizing pass. Returns 0 (rather than throwing) on any structural invalidity, so
    /// the sizing pass and the real pass agree on failure without duplicating the error message.
    /// </summary>
    private static int BuildInternal(ReadOnlySpan<int> codeLengths, int rootBits, Vp8LHuffmanCode[]? entries)
    {
        int alphabetSize = codeLengths.Length;
        Span<int> count = stackalloc int[MaxAllowedCodeLength + 1];
        Span<int> offset = stackalloc int[MaxAllowedCodeLength + 1];

        for (int symbol = 0; symbol < alphabetSize; symbol++)
        {
            int length = codeLengths[symbol];
            if (length > MaxAllowedCodeLength)
            {
                return 0;
            }

            count[length]++;
        }

        if (count[0] == alphabetSize)
        {
            // Every code length is zero: no symbols are actually encodable.
            return 0;
        }

        offset[1] = 0;
        for (int length = 1; length < MaxAllowedCodeLength; length++)
        {
            if (count[length] > (1 << length))
            {
                return 0;
            }

            offset[length + 1] = offset[length] + count[length];
        }

        var sorted = new int[alphabetSize];
        for (int symbol = 0; symbol < alphabetSize; symbol++)
        {
            int length = codeLengths[symbol];
            if (length > 0)
            {
                sorted[offset[length]++] = symbol;
            }
        }

        int totalSize = 1 << rootBits;

        // Special case: exactly one symbol is used at all. Its "code" is zero bits long — decoding always
        // returns that symbol without consuming anything from the stream.
        if (offset[MaxAllowedCodeLength] == 1)
        {
            entries?.AsSpan(0, totalSize).Fill(new Vp8LHuffmanCode { Bits = 0, Value = (ushort)sorted[0] });
            return totalSize;
        }

        int tableBits = rootBits;
        int tableSize = 1 << tableBits;
        uint low = 0xFFFFFFFFu;
        uint mask = (uint)(totalSize - 1);
        uint key = 0;
        int numNodes = 1;
        int numOpen = 1;
        int symbolIndex = 0;
        int tableStart = 0;

        // Fill the root table (lengths 1..rootBits).
        for (int length = 1, step = 2; length <= rootBits; length++, step <<= 1)
        {
            numOpen <<= 1;
            numNodes += numOpen;
            numOpen -= count[length];
            if (numOpen < 0)
            {
                return 0;
            }

            if (entries == null)
            {
                continue;
            }

            for (; count[length] > 0; count[length]--)
            {
                var code = new Vp8LHuffmanCode { Bits = (byte)length, Value = (ushort)sorted[symbolIndex++] };
                ReplicateValue(entries, (int)key, step, tableSize, code);
                key = GetNextKey(key, length);
            }
        }

        // Fill second-level tables and their root pointers (lengths rootBits+1..MaxAllowedCodeLength).
        for (int length = rootBits + 1, step = 2; length <= MaxAllowedCodeLength; length++, step <<= 1)
        {
            numOpen <<= 1;
            numNodes += numOpen;
            numOpen -= count[length];
            if (numOpen < 0)
            {
                return 0;
            }

            for (; count[length] > 0; count[length]--)
            {
                if ((key & mask) != low)
                {
                    if (entries != null)
                    {
                        tableStart += tableSize;
                    }

                    tableBits = NextTableBitSize(count, length, rootBits);
                    tableSize = 1 << tableBits;
                    totalSize += tableSize;
                    low = key & mask;

                    if (entries != null)
                    {
                        entries[low].Bits = (byte)(tableBits + rootBits);
                        entries[low].Value = (ushort)(tableStart - (int)low);
                    }
                }

                if (entries != null)
                {
                    var code = new Vp8LHuffmanCode { Bits = (byte)(length - rootBits), Value = (ushort)sorted[symbolIndex++] };
                    ReplicateValue(entries, tableStart + (int)(key >> rootBits), step, tableSize, code);
                }

                key = GetNextKey(key, length);
            }
        }

        if (numNodes != (2 * offset[MaxAllowedCodeLength]) - 1)
        {
            // The tree isn't "full" (Kraft's equality doesn't hold exactly) — under- or over-subscribed.
            return 0;
        }

        return totalSize;
    }

    /// <summary>Stores <paramref name="code"/> into <paramref name="table"/> at <c>offset, offset+step, offset+2*step, ..., offset+end-step</c>.</summary>
    private static void ReplicateValue(Vp8LHuffmanCode[] table, int offset, int step, int end, Vp8LHuffmanCode code)
    {
        int current = end;
        do
        {
            current -= step;
            table[offset + current] = code;
        }
        while (current > 0);
    }

    /// <summary>
    /// Given the current bit-reversed-prefix "key" of length <paramref name="length"/>, returns the next key
    /// in the LSB-first canonical sequence — equivalent to reversing the low <paramref name="length"/> bits
    /// of <paramref name="key"/>, incrementing, and reversing back, but computed directly without ever
    /// forming the reversed value.
    /// </summary>
    private static uint GetNextKey(uint key, int length)
    {
        uint step = 1u << (length - 1);
        while ((key & step) != 0)
        {
            step >>= 1;
        }

        return step != 0 ? (key & (step - 1)) + step : key;
    }

    /// <summary>
    /// Chooses how many bits wide the next second-level table should be: just enough to hold every code
    /// sharing the current root prefix, by looking ahead through longer lengths until this prefix's open
    /// branches are exhausted.
    /// </summary>
    private static int NextTableBitSize(ReadOnlySpan<int> count, int length, int rootBits)
    {
        int left = 1 << (length - rootBits);
        while (length < MaxAllowedCodeLength)
        {
            left -= count[length];
            if (left <= 0)
            {
                break;
            }

            length++;
            left <<= 1;
        }

        return length - rootBits;
    }
}
