using System.Runtime.CompilerServices;

namespace PeachImage.Formats.Jxl.Entropy;

/// <summary>
/// One entry of an ANS alias table. Dividing a value by the entry size picks an entry; the first
/// <see cref="Cutoff"/> offsets within it map to the entry's own symbol, the rest to <see cref="RightValue"/>.
/// </summary>
internal struct AliasEntry
{
    public byte Cutoff;
    public byte RightValue;
    public ushort Freq0;

    /// <summary>The offset of the right-hand side, already decremented by the cutoff.</summary>
    public ushort Offsets1;

    public ushort Freq1XorFreq0;
}

/// <summary>Builds and queries the alias tables used by JPEG XL's rANS entropy coder.</summary>
internal static class JxlAliasTable
{
    /// <summary>log2 of the ANS table size.</summary>
    public const int LogTableSize = 12;

    /// <summary>The ANS table size: all histograms sum to this.</summary>
    public const int TableSize = 1 << LogTableSize;

    /// <summary>The state an ANS stream must end in (and, for prefix codes, always holds).</summary>
    public const uint Signature = 0x13;

    /// <summary>
    /// Builds the alias table for <paramref name="distribution"/> (which must sum to <see cref="TableSize"/>) into
    /// <paramref name="table"/>, which has <c>1 &lt;&lt; logAlphaSize</c> entries.
    /// </summary>
    public static void Init(ReadOnlySpan<int> distribution, int logAlphaSize, Span<AliasEntry> table)
    {
        int tableSize = 1 << logAlphaSize;
        int range = TableSize;

        int length = distribution.Length;
        while (length > 0 && distribution[length - 1] == 0)
        {
            length--;
        }

        // An empty alphabet degenerates to one symbol with all the weight, so a hostile stream cannot crash the decoder.
        int[] dist = length == 0 ? [range] : distribution[..length].ToArray();
        if (dist.Length > tableSize)
        {
            throw new JxlDecodingException("The entropy-coder alphabet is larger than its table.");
        }

        int entrySize = range >> logAlphaSize;
        int singleSymbol = -1;
        int sum = 0;
        for (int symbol = 0; symbol < dist.Length; symbol++)
        {
            int v = dist[symbol];
            sum += v;
            if (v == TableSize)
            {
                singleSymbol = symbol;
            }
        }

        if (sum != range)
        {
            throw new JxlDecodingException("An entropy-coder histogram does not sum to the table size.");
        }

        // A single-symbol distribution must leave the ANS state unchanged when decoded.
        if (singleSymbol != -1)
        {
            for (int i = 0; i < tableSize; i++)
            {
                table[i] = new AliasEntry
                {
                    RightValue = (byte)singleSymbol,
                    Cutoff = 0,
                    Offsets1 = (ushort)(entrySize * i),
                    Freq0 = 0,
                    Freq1XorFreq0 = TableSize,
                };
            }

            return;
        }

        var underfull = new Stack<int>();
        var overfull = new Stack<int>();
        var cutoffs = new int[tableSize];
        for (int i = 0; i < dist.Length; i++)
        {
            cutoffs[i] = dist[i];
            if (cutoffs[i] > entrySize)
            {
                overfull.Push(i);
            }
            else if (cutoffs[i] < entrySize)
            {
                underfull.Push(i);
            }
        }

        for (int i = dist.Length; i < tableSize; i++)
        {
            cutoffs[i] = 0;
            underfull.Push(i);
        }

        // Move the excess of overfull entries into the holes of underfull ones.
        while (overfull.Count > 0)
        {
            int overfullIndex = overfull.Pop();
            if (underfull.Count == 0)
            {
                throw new JxlDecodingException("An entropy-coder histogram is inconsistent.");
            }

            int underfullIndex = underfull.Pop();
            int underfullBy = entrySize - cutoffs[underfullIndex];
            cutoffs[overfullIndex] -= underfullBy;
            table[underfullIndex].RightValue = (byte)overfullIndex;
            table[underfullIndex].Offsets1 = (ushort)cutoffs[overfullIndex];
            if (cutoffs[overfullIndex] < entrySize)
            {
                underfull.Push(overfullIndex);
            }
            else if (cutoffs[overfullIndex] > entrySize)
            {
                overfull.Push(overfullIndex);
            }
        }

        for (int i = 0; i < tableSize; i++)
        {
            if (cutoffs[i] == entrySize)
            {
                table[i].RightValue = (byte)i;
                table[i].Offsets1 = 0;
                table[i].Cutoff = 0;
            }
            else
            {
                table[i].Offsets1 = (ushort)(table[i].Offsets1 - cutoffs[i]);
                table[i].Cutoff = (byte)cutoffs[i];
            }

            int freq0 = i < dist.Length ? dist[i] : 0;
            int right = table[i].RightValue;
            int freq1 = right < dist.Length ? dist[right] : 0;
            table[i].Freq0 = (ushort)freq0;
            table[i].Freq1XorFreq0 = (ushort)(freq1 ^ freq0);
        }
    }

    /// <summary>Maps a value in <c>[0, TableSize)</c> to its symbol, the value's rank among that symbol's slots, and the symbol's frequency.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Symbol, int Offset, int Frequency) Lookup(ReadOnlySpan<AliasEntry> table, int value, int logEntrySize, int entrySizeMinusOne)
    {
        int index = value >> logEntrySize;
        int position = value & entrySizeMinusOne;
        ref readonly AliasEntry entry = ref table[index];
        bool greater = position >= entry.Cutoff;
        int symbol = greater ? entry.RightValue : index;
        int offset = (greater ? entry.Offsets1 : 0) + position;
        int frequency = entry.Freq0 ^ (greater ? entry.Freq1XorFreq0 : 0);
        return (symbol, offset, frequency);
    }
}
