namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>One entry of a built <see cref="Vp8LHuffmanTable"/>: either a leaf (<see cref="Bits"/> is the code's length and <see cref="Value"/> its symbol) or, only within the root level, a pointer to a second-level table (see the remarks on <see cref="Vp8LHuffmanTable"/>).</summary>
internal struct Vp8LHuffmanCode
{
    public byte Bits;
    public ushort Value;
}

/// <summary>
/// A built canonical-Huffman decoding table: a flat array holding the root (first-level) table followed by
/// zero or more second-level tables, exactly mirroring libwebp's <c>HuffmanCode</c> table layout (see
/// <see cref="Vp8LHuffmanTableBuilder"/> for how it's constructed). <see cref="Decode"/> is <c>O(1)</c>
/// worst-case: at most one root lookup plus one second-level lookup, no bit-by-bit tree walk.
/// </summary>
/// <remarks>
/// Root entries come in two flavors, distinguished purely by comparing <see cref="Vp8LHuffmanCode.Bits"/>
/// against <see cref="RootBits"/>: if <c>Bits &lt;= RootBits</c> the entry is a genuine leaf (the code was
/// short enough to fit directly in the root table, possibly replicated across every slot sharing its
/// low-order-bit prefix). If <c>Bits &gt; RootBits</c> the entry is a pointer: <see cref="Vp8LHuffmanCode.Bits"/>
/// holds the *total* code length (root + extra), and <see cref="Vp8LHuffmanCode.Value"/> holds an offset
/// (relative to the root slot's own index) to where that code's second-level table begins in the same flat
/// array. Entries actually stored *within* a second-level table always describe a leaf directly (their
/// <see cref="Vp8LHuffmanCode.Bits"/> is only the *additional* bits beyond the root, and can never itself be
/// a further pointer — VP8L's two-level scheme never nests deeper than one extra level).
/// </remarks>
internal sealed class Vp8LHuffmanTable
{
    public required Vp8LHuffmanCode[] Entries { get; init; }

    public required int RootBits { get; init; }

    /// <summary>The longest code VP8L permits, so one peek of this width always covers both levels of the lookup — every root entry's total <see cref="Vp8LHuffmanCode.Bits"/> is bounded by it, and <see cref="RootBits"/> (7 or 8) is well under it.</summary>
    private const int MaxCodeLength = Internal.WebpDecodingLimits.MaxHuffmanCodeLength;

    /// <summary>Decodes the next symbol from <paramref name="reader"/>, consuming exactly as many bits as its code is long.</summary>
    /// <remarks>
    /// Peeks the maximum code length once and slices both levels out of that single window, rather than
    /// peeking <see cref="RootBits"/> and then re-peeking a wider window for a second-level code. Peeking is
    /// non-destructive — <see cref="Vp8LBitReader.SkipBits"/> alone decides what is actually consumed, and it
    /// is also what drives <see cref="Vp8LBitReader.IsOverBudget"/> — so widening the peek changes nothing
    /// observable. Mirrors libwebp's <c>VP8LPrefetchBits</c>/<c>VP8LGetBitsUnsafe</c> pairing.
    /// </remarks>
    public int Decode(Vp8LBitReader reader)
    {
        int rootBits = RootBits;
        uint window = reader.PeekBits(MaxCodeLength);
        uint low = window & ((1u << rootBits) - 1);
        var entries = Entries;
        var root = entries[low];

        if (root.Bits <= rootBits)
        {
            reader.SkipBits(root.Bits);
            return root.Value;
        }

        int extraBits = root.Bits - rootBits;
        uint secondaryOffset = (window >> rootBits) & ((1u << extraBits) - 1);
        var second = entries[(int)low + root.Value + (int)secondaryOffset];

        reader.SkipBits(rootBits + second.Bits);
        return second.Value;
    }
}
