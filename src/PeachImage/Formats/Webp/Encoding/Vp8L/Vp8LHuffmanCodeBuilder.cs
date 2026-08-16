namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>
/// Builds length-limited canonical Huffman codes for a symbol-frequency histogram: the encoder-side
/// counterpart of <see cref="Decoding.Vp8L.Vp8LHuffmanTableBuilder"/>, which builds a *decoding* table from
/// already-known code lengths. Uses a plain unrestricted-Huffman-tree-then-clamp-and-rebalance approach
/// (the classic zlib/DEFLATE <c>gen_bitlen</c> technique) rather than package-merge — simpler to implement
/// correctly, at the cost of being slightly (~1-2%) less compact than a provably optimal length-limited code.
/// </summary>
internal static class Vp8LHuffmanCodeBuilder
{
    /// <summary>
    /// Computes one code length per symbol (0 = symbol unused) from <paramref name="freq"/>, limited to
    /// <paramref name="maxLength"/> bits. When no symbol is used at all, declares an arbitrary single symbol
    /// (index 0) with length 1 — VP8L still requires a structurally valid tree even for an alphabet the
    /// token stream never actually reaches (e.g. the distance alphabet, for an image with no backward
    /// references at all).
    /// </summary>
    public static void BuildCodeLengths(ReadOnlySpan<int> freq, Span<int> codeLengths, int maxLength)
    {
        codeLengths.Clear();

        var used = new List<int>();
        for (int symbol = 0; symbol < freq.Length; symbol++)
        {
            if (freq[symbol] > 0)
            {
                used.Add(symbol);
            }
        }

        if (used.Count == 0)
        {
            codeLengths[0] = 1;
            return;
        }

        if (used.Count == 1)
        {
            codeLengths[used[0]] = 1;
            return;
        }

        int n = used.Count;
        int[] depth = BuildTreeAndComputeLeafDepths(freq, used, out int maxDepth);

        int countLength = Math.Max(maxDepth, maxLength) + 2;
        var count = new int[countLength];
        for (int i = 0; i < n; i++)
        {
            count[depth[i]]++;
        }

        if (maxDepth > maxLength)
        {
            LimitCodeLengths(count, maxLength);
        }

        AssignLengthsByFrequency(freq, used, count, maxLength, codeLengths);
    }

    /// <summary>Builds an ordinary (unrestricted) Huffman tree via the standard greedy min-priority-queue merge, and returns each of the first <c>used.Count</c> node indices' (i.e. each leaf's) depth from the root.</summary>
    private static int[] BuildTreeAndComputeLeafDepths(ReadOnlySpan<int> freq, List<int> used, out int maxDepth)
    {
        int n = used.Count;
        int nodeCount = (2 * n) - 1;
        var weight = new long[nodeCount];
        var left = new int[nodeCount];
        var right = new int[nodeCount];

        for (int i = 0; i < nodeCount; i++)
        {
            left[i] = -1;
            right[i] = -1;
        }

        var queue = new PriorityQueue<int, long>(n);
        for (int i = 0; i < n; i++)
        {
            weight[i] = freq[used[i]];
            queue.Enqueue(i, weight[i]);
        }

        int nextNode = n;
        while (queue.Count > 1)
        {
            int a = queue.Dequeue();
            int b = queue.Dequeue();
            int node = nextNode++;
            weight[node] = weight[a] + weight[b];
            left[node] = a;
            right[node] = b;
            queue.Enqueue(node, weight[node]);
        }

        int root = queue.Dequeue();

        var depth = new int[nodeCount];
        var stack = new Stack<int>();
        stack.Push(root);
        maxDepth = 0;

        while (stack.Count > 0)
        {
            int node = stack.Pop();
            if (left[node] < 0)
            {
                maxDepth = Math.Max(maxDepth, depth[node]);
                continue;
            }

            depth[left[node]] = depth[node] + 1;
            depth[right[node]] = depth[node] + 1;
            stack.Push(left[node]);
            stack.Push(right[node]);
        }

        return depth;
    }

    /// <summary>
    /// Length-limiting repair: folds every code longer than <paramref name="maxLength"/> down to exactly
    /// <paramref name="maxLength"/> (which over-subscribes the tree), then repeatedly grafts one overflow leaf
    /// onto some shorter code of length <c>L</c> — turning that one real leaf into two leaves of length
    /// <c>L+1</c> (one is the relocated overflow leaf, the other is the original leaf moved one level down) —
    /// until the excess reaches exactly zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tracks the excess in exact integer "Kraft units" (of a length-<paramref name="maxLength"/> leaf, so the
    /// tree's total budget is <c>2^maxLength</c> units): folding always leaves the tree over-subscribed by
    /// some positive number of units, computed directly rather than assumed. Each graft move —
    /// <c>count[bits]--; count[bits+1] += 2; count[maxLength]--;</c> — removes <em>exactly</em> 1 unit
    /// regardless of which <c>bits</c> was used, verified algebraically for both the general case and the
    /// <c>bits+1 == maxLength</c> edge case (where the <c>+2</c> and <c>-1</c> land on the same slot and net
    /// to <c>+1</c>): <c>-2^-bits + 2*2^-(bits+1) - 2^-maxLength = -2^-bits + 2^-bits - 2^-maxLength =
    /// -2^-maxLength</c>. Because every move removes the same fixed amount, decrementing the running excess by
    /// exactly 1 per move can never overshoot into an under-subscribed (invalid) tree — unlike an earlier
    /// version of this method that used a simpler <c>count[bits]--; count[bits+1]++;</c> move (no
    /// <c>count[maxLength]--</c>), whose per-move removal amount grows the further <c>bits</c> is from
    /// <paramref name="maxLength"/> and so could remove more than the remaining excess in one step once the
    /// finer buckets ran dry — reproducible on real (non-synthetic) image data with a large color cache and a
    /// long tail of low-frequency symbols, where it silently left the tree under-subscribed.
    /// </para>
    /// <para>
    /// Always prefers the largest available <c>bits</c> (closest to <paramref name="maxLength"/>) first — an
    /// arbitrary but conventional tie-break, not load-bearing for correctness now that every move's effect is
    /// fixed-size.
    /// </para>
    /// </remarks>
    private static void LimitCodeLengths(int[] count, int maxLength)
    {
        for (int len = count.Length - 1; len > maxLength; len--)
        {
            count[maxLength] += count[len];
            count[len] = 0;
        }

        long totalUnits = 0;
        for (int len = 1; len <= maxLength; len++)
        {
            totalUnits += (long)count[len] << (maxLength - len);
        }

        long excessUnits = totalUnits - (1L << maxLength);

        while (excessUnits > 0)
        {
            int bits = maxLength - 1;
            while (count[bits] == 0)
            {
                bits--;
            }

            count[bits]--;
            count[bits + 1] += 2;
            count[maxLength]--;
            excessUnits--;
        }
    }

    /// <summary>Pairs the lowest-frequency symbols with the longest lengths and the highest-frequency symbols with the shortest — optimal for any fixed, Kraft-satisfying length histogram.</summary>
    private static void AssignLengthsByFrequency(ReadOnlySpan<int> freq, List<int> used, int[] count, int maxLength, Span<int> codeLengths)
    {
        int n = used.Count;
        var order = new int[n];
        var usedFreq = new int[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
            usedFreq[i] = freq[used[i]];
        }

        // Array.Sort's comparison delegate can't capture a ref-like ReadOnlySpan<int>, so the needed
        // frequencies are copied into a plain array first.
        Array.Sort(order, (a, b) =>
        {
            int cmp = usedFreq[a].CompareTo(usedFreq[b]);
            return cmp != 0 ? cmp : used[a].CompareTo(used[b]);
        });

        int pos = 0;
        for (int len = maxLength; len >= 1; len--)
        {
            for (int c = 0; c < count[len]; c++)
            {
                int usedIndex = order[pos++];
                codeLengths[used[usedIndex]] = len;
            }
        }
    }

    /// <summary>Derives canonical LSB-first-emittable codes from already-built code lengths (RFC 1951 §3.2.2), bit-reversed so <see cref="Vp8LBitWriter.WriteBits"/> writes them in the order <see cref="Decoding.Vp8L.Vp8LHuffmanTableBuilder"/>'s table construction expects.</summary>
    public static void AssignCanonicalCodes(ReadOnlySpan<int> codeLengths, Span<uint> codesLsbFirst)
    {
        int maxLen = 0;
        for (int i = 0; i < codeLengths.Length; i++)
        {
            if (codeLengths[i] > maxLen)
            {
                maxLen = codeLengths[i];
            }
        }

        if (maxLen == 0)
        {
            return;
        }

        Span<int> count = maxLen + 1 <= 32 ? stackalloc int[maxLen + 1] : new int[maxLen + 1];
        for (int i = 0; i < codeLengths.Length; i++)
        {
            int len = codeLengths[i];
            if (len > 0)
            {
                count[len]++;
            }
        }

        Span<uint> nextCode = maxLen + 1 <= 32 ? stackalloc uint[maxLen + 1] : new uint[maxLen + 1];
        uint code = 0;
        for (int len = 1; len <= maxLen; len++)
        {
            code = (code + (uint)count[len - 1]) << 1;
            nextCode[len] = code;
        }

        for (int symbol = 0; symbol < codeLengths.Length; symbol++)
        {
            int len = codeLengths[symbol];
            if (len > 0)
            {
                uint canonical = nextCode[len]++;
                codesLsbFirst[symbol] = ReverseBits(canonical, len);
            }
        }
    }

    private static uint ReverseBits(uint value, int bitCount)
    {
        uint reversed = 0;
        for (int i = 0; i < bitCount; i++)
        {
            reversed |= ((value >> i) & 1) << (bitCount - 1 - i);
        }

        return reversed;
    }
}
