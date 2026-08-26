namespace PeachImage.Formats.Jpeg.Entropy;

/// <summary>
/// Builds a canonical Huffman code-length assignment from observed symbol frequencies (ITU-T.81 Annex K.2's
/// "Procedure for computing Huffman code lengths" / K.3's code-length-limiting adjustment — the same
/// algorithm libjpeg's <c>jpeg_gen_optimal_table</c> implements). Unlike <see cref="StandardHuffmanTables"/>
/// (a fixed table tuned for typical *baseline* symbol distributions), this covers exactly the symbols a
/// given scan actually uses — required for progressive AC scans, whose EOB-run-length symbols (run
/// lengths 1-14 with size 0) have no entries at all in the standard tables.
/// </summary>
internal static class HuffmanTableOptimizer
{
    // 256 real symbols plus one reserved "dummy" symbol (always frequency 1) that guarantees at least one
    // code is never assigned to a real symbol — the standard trick (Annex K.2) for ensuring no real symbol
    // ends up coded as all 1-bits, which would be indistinguishable from padding/fill bits.
    private const int DummySymbol = 256;
    private const int SymbolCount = 257;

    // Worst-case Huffman tree depth for 257 symbols (a pathologically skewed, Fibonacci-like frequency
    // distribution) is 256 — size generously above that so the length-limiting pass below never indexes
    // out of bounds regardless of input.
    private const int MaxRawCodeLength = 300;

    /// <summary>Builds (Counts, Values) — the BITS/HUFFVAL shape a DHT segment and <see cref="HuffmanEncodingTable"/> expect — from per-symbol usage counts (index 0-255).</summary>
    public static (byte[] Counts, byte[] Values) Build(int[] frequencies)
    {
        var freq = new long[SymbolCount];
        for (int i = 0; i < 256; i++)
        {
            freq[i] = frequencies[i];
        }

        freq[DummySymbol] = 1;

        var codeSize = new int[SymbolCount];
        var others = new int[SymbolCount];
        Array.Fill(others, -1);

        while (true)
        {
            int c1 = FindSmallest(freq, -1);
            int c2 = FindSmallest(freq, c1);
            if (c2 < 0)
            {
                break;
            }

            freq[c1] += freq[c2];
            freq[c2] = 0;

            codeSize[c1]++;
            while (others[c1] >= 0)
            {
                c1 = others[c1];
                codeSize[c1]++;
            }

            others[c1] = c2;

            codeSize[c2]++;
            while (others[c2] >= 0)
            {
                c2 = others[c2];
                codeSize[c2]++;
            }
        }

        var bits = new int[MaxRawCodeLength + 1];
        for (int i = 0; i < SymbolCount; i++)
        {
            if (codeSize[i] > 0)
            {
                bits[codeSize[i]]++;
            }
        }

        // JPEG caps code length at 16 bits — redistribute any longer codes per Annex K.3: pull two codes
        // of length i down to one of length i-1, donating the freed capacity to the shortest length (j)
        // that still has room by lengthening one of its codes to j+1.
        for (int i = MaxRawCodeLength; i > 16; i--)
        {
            while (bits[i] > 0)
            {
                int j = i - 2;
                while (bits[j] == 0)
                {
                    j--;
                }

                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        }

        int dummyLength = 16;
        while (bits[dummyLength] == 0)
        {
            dummyLength--;
        }

        bits[dummyLength]--;

        var counts = new byte[16];
        for (int length = 1; length <= 16; length++)
        {
            counts[length - 1] = (byte)bits[length];
        }

        // Symbols are listed in order of their *raw* (pre-length-limiting) codeSize, not the redistributed
        // per-length counts in `bits`/`counts` above -- codeSize is never itself adjusted by the redistribution
        // loop, only the aggregate histogram is. A symbol can legitimately have codeSize > 16 here for a
        // sufficiently skewed distribution (the whole reason the redistribution loop exists), so this must
        // scan the full raw range, not stop at 16: capping at 16 would silently drop such symbols from
        // `values` while `counts` (built from the redistributed, sum-preserving `bits`) still allocates a
        // code for them, desynchronizing the two and leaving HuffmanEncodingTable to walk off the end of
        // `values`. Per ITU-T.81 Annex K.3 / libjpeg's jpeg_gen_optimal_table (whose own comment calls this
        // "not real clear... but the JPEG spec seems to think this works"), sorting purely by raw codeSize is
        // sufficient: each symbol's *actual* transmitted code length is implicitly determined by its position
        // in this list once `counts` is consumed length-by-length, not by its raw codeSize value directly.
        var values = new List<byte>();
        for (int length = 1; length <= MaxRawCodeLength; length++)
        {
            for (int symbol = 0; symbol < 256; symbol++)
            {
                if (codeSize[symbol] == length)
                {
                    values.Add((byte)symbol);
                }
            }
        }

        return (counts, values.ToArray());
    }

    private static int FindSmallest(long[] freq, int exclude)
    {
        int found = -1;
        long min = long.MaxValue;
        for (int i = 0; i < freq.Length; i++)
        {
            if (i != exclude && freq[i] > 0 && freq[i] <= min)
            {
                min = freq[i];
                found = i;
            }
        }

        return found;
    }
}
