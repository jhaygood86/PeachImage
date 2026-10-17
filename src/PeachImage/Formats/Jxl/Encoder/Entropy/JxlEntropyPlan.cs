using System.Buffers;
using System.Numerics;
using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Entropy;

namespace PeachImage.Formats.Jxl.Encoder.Entropy;

/// <summary>A value to entropy-code under an (unclustered) context.</summary>
internal readonly record struct JxlToken(int Context, uint Value);

/// <summary>
/// A growable list of <see cref="JxlToken"/>s, one stream's worth of symbols in decode order, packed into four bytes
/// each: a 16-bit context and a 16-bit value, where the rare value of 65535 or more is kept in a side list.
/// </summary>
internal sealed class JxlTokenList
{
    private const ushort Escape = ushort.MaxValue;

    private ushort[] _contexts = new ushort[256];
    private ushort[] _values = new ushort[256];
    private List<uint>? _overflow;
    private int _count;

    public int Count => _count;

    public void Add(int context, uint value)
    {
        if ((uint)context > ushort.MaxValue)
        {
            throw new JxlEncodingException("An entropy context is out of range.");
        }

        if (_count == _contexts.Length)
        {
            Array.Resize(ref _contexts, _contexts.Length * 2);
            Array.Resize(ref _values, _values.Length * 2);
        }

        _contexts[_count] = (ushort)context;
        if (value >= Escape)
        {
            (_overflow ??= []).Add(value);
            _values[_count] = Escape;
        }
        else
        {
            _values[_count] = (ushort)value;
        }

        _count++;
    }

    public void Clear()
    {
        _count = 0;
        _overflow?.Clear();
    }

    public Enumerator GetEnumerator() => new(this);

    /// <summary>Walks the tokens in insertion order.</summary>
    public struct Enumerator
    {
        private readonly JxlTokenList _list;
        private int _index;
        private int _overflowIndex;

        internal Enumerator(JxlTokenList list)
        {
            _list = list;
            _index = -1;
            _overflowIndex = 0;
        }

        public JxlToken Current { get; private set; }

        public bool MoveNext()
        {
            if (++_index >= _list._count)
            {
                return false;
            }

            ushort value = _list._values[_index];
            Current = new JxlToken(_list._contexts[_index], value == Escape ? _list._overflow![_overflowIndex++] : value);
            return true;
        }
    }
}

/// <summary>
/// The chosen rANS entropy code for a set of token streams that share histograms: the context-to-histogram map
/// found by clustering, the normalized histograms and their alias/inverse tables. Build it from the tokens of all
/// streams that will use it, write its header once, then write each stream with <see cref="WriteStream"/>.
/// </summary>
internal sealed class JxlEntropyPlan
{
    /// <summary>log2 of the alphabet size (the widest the format allows for rANS).</summary>
    public const int LogAlphaSize = 8;

    private const int AlphabetSize = 1 << LogAlphaSize;
    private const int MaxClusters = 256;

    // Rough per-histogram cost in bits for the header, used to decide whether merging two histograms pays off.
    private const double HistogramOverheadBits = 24;

    private readonly HybridUintConfig _config;
    private readonly byte[] _contextMap;
    private readonly int[][] _normalized;
    private readonly ushort[][] _slotTables;
    private readonly int[][] _cumulative;

    private JxlEntropyPlan(HybridUintConfig config, byte[] contextMap, int[][] normalized)
    {
        _config = config;
        _contextMap = contextMap;
        _normalized = normalized;
        _slotTables = new ushort[normalized.Length][];
        _cumulative = new int[normalized.Length][];
        var alias = new AliasEntry[AlphabetSize];
        for (int c = 0; c < normalized.Length; c++)
        {
            BuildSlotTable(normalized[c], alias, out _cumulative[c], out _slotTables[c]);
        }
    }

    /// <summary>The number of distinct histograms (clusters).</summary>
    public int NumClusters => _normalized.Length;

    /// <summary>Builds a plan for <paramref name="numContexts"/> contexts from a single stream's tokens.</summary>
    public static JxlEntropyPlan Create(int numContexts, JxlTokenList stream) => Create(numContexts, [stream]);

    /// <summary>Builds a plan from several streams that will share the resulting code.</summary>
    public static JxlEntropyPlan Create(int numContexts, IReadOnlyList<JxlTokenList> streams)
    {
        var config = JxlHybridUintEncoder.Default;
        var histograms = new long[numContexts][];
        foreach (var stream in streams)
        {
            Accumulate(histograms, config, stream);
        }

        return FromHistograms(config, histograms);
    }

    /// <summary>The cluster (histogram index) an unclustered context maps to.</summary>
    public int ClusterOf(int context) => _contextMap[context];

    /// <summary>
    /// Writes the entropy-code header for the contexts this plan was built for: LZ77 off, the context map when
    /// there is more than one context, rANS, the hybrid-integer configs and the histograms.
    /// </summary>
    public void WriteHeader(JxlBitWriter writer)
    {
        writer.WriteBool(false); // no LZ77
        if (_contextMap.Length > 1)
        {
            WriteContextMap(writer, _contextMap, _normalized.Length);
        }

        writer.WriteBool(false); // rANS, not prefix codes
        writer.WriteBits(LogAlphaSize - 5, 2);
        for (int i = 0; i < _normalized.Length; i++)
        {
            JxlHybridUintEncoder.WriteConfig(writer, _config, LogAlphaSize);
        }

        foreach (int[] histogram in _normalized)
        {
            JxlAnsHistogramWriter.Write(writer, histogram);
        }
    }

    /// <summary>
    /// Writes one stream: the 32-bit initial rANS state, then each token's refill word (when the decoder will read one)
    /// followed by its raw extra bits, in decode order.
    /// </summary>
    public void WriteStream(JxlBitWriter writer, JxlTokenList tokens)
    {
        int n = tokens.Count;
        var symbols = ArrayPool<ushort>.Shared.Rent(Math.Max(1, n));
        var clusters = ArrayPool<byte>.Shared.Rent(Math.Max(1, n));
        var extra = ArrayPool<uint>.Shared.Rent(Math.Max(1, n));
        var extraBits = ArrayPool<byte>.Shared.Rent(Math.Max(1, n));
        var refill = ArrayPool<int>.Shared.Rent(Math.Max(1, n));
        try
        {
            int filled = 0;
            foreach (var t in tokens)
            {
                uint token = JxlHybridUintEncoder.Split(_config, t.Value, out extra[filled], out int bits);
                symbols[filled] = (ushort)token;
                extraBits[filled] = (byte)bits;
                clusters[filled] = _contextMap[t.Context];
                filled++;
            }

            // Encode backwards: the state a decoder starts from is the state after encoding the first token.
            uint state = JxlAliasTable.Signature << 16;
            for (int i = n - 1; i >= 0; i--)
            {
                int cluster = clusters[i];
                int symbol = symbols[i];
                int[] histogram = _normalized[cluster];
                int frequency = symbol < histogram.Length ? histogram[symbol] : 0;
                if (frequency == 0)
                {
                    throw new JxlEncodingException("A token has no probability in its histogram.");
                }

                if ((ulong)state >= (ulong)frequency << 20)
                {
                    refill[i] = (int)(state & 0xFFFF);
                    state >>= 16;
                }
                else
                {
                    refill[i] = -1;
                }

                uint quotient = state / (uint)frequency;
                uint remainder = state - (quotient * (uint)frequency);
                uint slot = _slotTables[cluster][_cumulative[cluster][symbol] + (int)remainder];
                state = (quotient << JxlAliasTable.LogTableSize) | slot;
            }

            writer.WriteBits(state, 32);
            for (int i = 0; i < n; i++)
            {
                if (refill[i] >= 0)
                {
                    writer.WriteBits((uint)refill[i], 16);
                }

                writer.WriteBits(extra[i], extraBits[i]);
            }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(symbols);
            ArrayPool<byte>.Shared.Return(clusters);
            ArrayPool<uint>.Shared.Return(extra);
            ArrayPool<byte>.Shared.Return(extraBits);
            ArrayPool<int>.Shared.Return(refill);
        }
    }

    private static void Accumulate(long[][] histograms, in HybridUintConfig config, JxlTokenList tokens)
    {
        foreach (var token in tokens)
        {
            uint symbol = JxlHybridUintEncoder.Split(config, token.Value, out _, out _);
            (histograms[token.Context] ??= new long[AlphabetSize])[symbol]++;
        }
    }

    private static JxlEntropyPlan FromHistograms(in HybridUintConfig config, long[][] histograms)
    {
        int numContexts = histograms.Length;
        var contextMap = new byte[numContexts];

        // Agglomerative clustering of the used contexts, merging while it lowers the estimated total size.
        var clusterHistograms = new List<long[]>();
        var clusterMembers = new List<List<int>>();
        for (int c = 0; c < numContexts; c++)
        {
            if (histograms[c] is { } h)
            {
                clusterHistograms.Add((long[])h.Clone());
                clusterMembers.Add([c]);
            }
        }

        if (clusterHistograms.Count == 0)
        {
            clusterHistograms.Add(new long[AlphabetSize]);
            clusterMembers.Add([]);
        }

        Cluster(clusterHistograms, clusterMembers);

        // Number clusters in order of first use so the context map reads naturally (and compresses well).
        var order = new List<int>(clusterHistograms.Count);
        var clusterOfContext = new int[numContexts];
        Array.Fill(clusterOfContext, -1);
        for (int k = 0; k < clusterMembers.Count; k++)
        {
            foreach (int c in clusterMembers[k])
            {
                clusterOfContext[c] = k;
            }
        }

        var newIndex = new int[clusterHistograms.Count];
        Array.Fill(newIndex, -1);
        for (int c = 0; c < numContexts; c++)
        {
            int k = clusterOfContext[c];
            if (k < 0)
            {
                // Unused context: any valid histogram will do.
                contextMap[c] = 0;
                continue;
            }

            if (newIndex[k] < 0)
            {
                newIndex[k] = order.Count;
                order.Add(k);
            }

            contextMap[c] = (byte)newIndex[k];
        }

        if (order.Count == 0)
        {
            order.Add(0);
        }

        var normalized = new int[order.Count][];
        for (int i = 0; i < order.Count; i++)
        {
            normalized[i] = JxlAnsHistogramWriter.Normalize(clusterHistograms[order[i]]);
        }

        return new JxlEntropyPlan(config, contextMap, normalized);
    }

    // Entropy-coded size of a histogram in bits, plus a fixed header allowance.
    private static double Cost(long[] h)
    {
        long total = 0;
        int distinct = 0;
        foreach (long v in h)
        {
            total += v;
            if (v > 0)
            {
                distinct++;
            }
        }

        if (total == 0)
        {
            return 0;
        }

        double bits = 0;
        double logTotal = Math.Log2(total);
        foreach (long v in h)
        {
            if (v > 0)
            {
                bits += v * (logTotal - Math.Log2(v));
            }
        }

        return bits + HistogramOverheadBits + (distinct * 4);
    }

    private static void Cluster(List<long[]> histograms, List<List<int>> members)
    {
        if (histograms.Count <= 24)
        {
            ClusterPairwise(histograms, members);
        }
        else
        {
            ClusterGreedy(histograms, members);
        }
    }

    // Single pass over the contexts, busiest first: each joins the cluster it costs least to add to, or starts its own
    // when that is cheaper. Linear in contexts times clusters, unlike the exhaustive pairwise search.
    private static void ClusterGreedy(List<long[]> histograms, List<List<int>> members)
    {
        int n = histograms.Count;
        var order = new int[n];
        var totals = new long[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
            foreach (long v in histograms[i])
            {
                totals[i] += v;
            }
        }

        Array.Sort(order, (a, b) => totals[b].CompareTo(totals[a]));
        var clusterHistograms = new List<long[]>();
        var clusterMembers = new List<List<int>>();
        var clusterCosts = new List<double>();
        var merged = new long[AlphabetSize];
        foreach (int index in order)
        {
            long[] h = histograms[index];
            double own = Cost(h);
            int best = -1;
            double bestDelta = double.PositiveInfinity;
            for (int k = 0; k < clusterHistograms.Count; k++)
            {
                for (int s = 0; s < AlphabetSize; s++)
                {
                    merged[s] = clusterHistograms[k][s] + h[s];
                }

                double delta = Cost(merged) - clusterCosts[k];
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = k;
                }
            }

            if (best >= 0 && (bestDelta < own || clusterHistograms.Count >= MaxClusters))
            {
                for (int s = 0; s < AlphabetSize; s++)
                {
                    clusterHistograms[best][s] += h[s];
                }

                clusterCosts[best] = Cost(clusterHistograms[best]);
                clusterMembers[best].AddRange(members[index]);
            }
            else
            {
                clusterHistograms.Add((long[])h.Clone());
                clusterCosts.Add(own);
                clusterMembers.Add([.. members[index]]);
            }
        }

        histograms.Clear();
        histograms.AddRange(clusterHistograms);
        members.Clear();
        members.AddRange(clusterMembers);
    }

    private static void ClusterPairwise(List<long[]> histograms, List<List<int>> members)
    {
        while (histograms.Count > 1)
        {
            int bestA = -1;
            int bestB = -1;
            double bestDelta = histograms.Count > MaxClusters ? double.PositiveInfinity : 0;
            var costs = new double[histograms.Count];
            for (int i = 0; i < costs.Length; i++)
            {
                costs[i] = Cost(histograms[i]);
            }

            var merged = new long[AlphabetSize];
            for (int a = 0; a < histograms.Count; a++)
            {
                for (int b = a + 1; b < histograms.Count; b++)
                {
                    for (int s = 0; s < AlphabetSize; s++)
                    {
                        merged[s] = histograms[a][s] + histograms[b][s];
                    }

                    double delta = Cost(merged) - costs[a] - costs[b];
                    if (delta < bestDelta || (bestA < 0 && histograms.Count > MaxClusters))
                    {
                        bestDelta = delta;
                        bestA = a;
                        bestB = b;
                    }
                }
            }

            if (bestA < 0)
            {
                return;
            }

            for (int s = 0; s < AlphabetSize; s++)
            {
                histograms[bestA][s] += histograms[bestB][s];
            }

            members[bestA].AddRange(members[bestB]);
            histograms.RemoveAt(bestB);
            members.RemoveAt(bestB);
        }
    }

    // Inverts the decoder's alias lookup: for every (symbol, rank-within-symbol) the table slot that decodes to it.
    private static void BuildSlotTable(int[] normalized, AliasEntry[] alias, out int[] cumulative, out ushort[] slots)
    {
        JxlAliasTable.Init(normalized, LogAlphaSize, alias);
        cumulative = new int[normalized.Length + 1];
        for (int s = 0; s < normalized.Length; s++)
        {
            cumulative[s + 1] = cumulative[s] + normalized[s];
        }

        slots = new ushort[JxlAliasTable.TableSize];
        int logEntrySize = JxlAliasTable.LogTableSize - LogAlphaSize;
        int entryMask = (1 << logEntrySize) - 1;
        for (int slot = 0; slot < JxlAliasTable.TableSize; slot++)
        {
            var (symbol, offset, _) = JxlAliasTable.Lookup(alias, slot, logEntrySize, entryMask);
            slots[cumulative[symbol] + offset] = (ushort)slot;
        }
    }

    internal static void WriteContextMap(JxlBitWriter writer, byte[] map, int numClusters)
    {
        if (numClusters == 1)
        {
            writer.WriteBool(true);
            writer.WriteBits(0, 2);
            return;
        }

        int bitsPerEntry = 32 - BitOperations.LeadingZeroCount((uint)(numClusters - 1));
        if (bitsPerEntry <= 3 && map.Length * bitsPerEntry <= 48)
        {
            writer.WriteBool(true);
            writer.WriteBits((uint)bitsPerEntry, 2);
            foreach (byte entry in map)
            {
                writer.WriteBits(entry, bitsPerEntry);
            }

            return;
        }

        writer.WriteBool(false);
        var plain = new JxlTokenList();
        foreach (byte entry in map)
        {
            plain.Add(0, entry);
        }

        var moveToFront = new JxlTokenList();
        Span<byte> list = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
        {
            list[i] = (byte)i;
        }

        foreach (byte entry in map)
        {
            int index = list.IndexOf(entry);
            moveToFront.Add(0, (uint)index);
            if (index != 0)
            {
                list[..index].CopyTo(list.Slice(1, index));
                list[0] = entry;
            }
        }

        var plainPlan = Create(1, [plain]);
        var mtfPlan = Create(1, [moveToFront]);
        var (useMtf, tokens, plan) = EstimateBits(plainPlan, plain) <= EstimateBits(mtfPlan, moveToFront)
            ? (false, plain, plainPlan)
            : (true, moveToFront, mtfPlan);
        writer.WriteBool(useMtf);
        plan.WriteHeader(writer);
        plan.WriteStream(writer, tokens);
    }

    private static double EstimateBits(JxlEntropyPlan plan, JxlTokenList tokens)
    {
        var config = plan._config;
        var counts = new long[AlphabetSize];
        double extraBits = 0;
        foreach (var token in tokens)
        {
            counts[JxlHybridUintEncoder.Split(config, token.Value, out _, out int bits)]++;
            extraBits += bits;
        }

        double total = tokens.Count;
        double bitsTotal = extraBits;
        foreach (long c in counts)
        {
            if (c > 0)
            {
                bitsTotal += c * (Math.Log2(total) - Math.Log2(c));
            }
        }

        return bitsTotal;
    }
}
