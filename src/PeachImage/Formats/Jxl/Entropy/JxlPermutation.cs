using System.Numerics;

namespace PeachImage.Formats.Jxl.Entropy;

/// <summary>Reads permutations coded as an entropy-coded Lehmer code (used for TOC ordering and AC coefficient orders).</summary>
internal static class JxlPermutation
{
    /// <summary>The number of entropy contexts a permutation stream uses.</summary>
    public const int NumContexts = 8;

    /// <summary>
    /// Reads a permutation of <paramref name="size"/> elements whose first <paramref name="skip"/> entries are the identity,
    /// decoding Lehmer-code digits from <paramref name="reader"/> (a stream with <see cref="NumContexts"/> contexts).
    /// </summary>
    public static int[] Read(ref Bitstream.JxlBitReader br, JxlSymbolReader reader, byte[] contextMap, int skip, int size)
    {
        var lehmer = new uint[size];
        uint end = reader.ReadHybridUint(Context((uint)size), ref br, contextMap) + (uint)skip;
        if (end > size)
        {
            throw new JxlDecodingException("Invalid permutation size.");
        }

        uint last = 0;
        for (int i = skip; i < end; i++)
        {
            lehmer[i] = reader.ReadHybridUint(Context(last), ref br, contextMap);
            last = lehmer[i];
            if (lehmer[i] >= size - i)
            {
                throw new JxlDecodingException("Invalid Lehmer code.");
            }
        }

        return DecodeLehmerCode(lehmer);
    }

    // The hybrid-integer token (split exponent 0) of the value, capped at the number of contexts.
    private static int Context(uint value)
    {
        int token = value == 0 ? 0 : 1 + BitOperations.Log2(value);
        return Math.Min(token, NumContexts - 1);
    }

    internal static int[] DecodeLehmerCode(uint[] code)
    {
        int n = code.Length;
        int log2n = n <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(n - 1));
        int paddedN = 1 << log2n;

        var temp = new uint[paddedN];
        for (int i = 0; i < paddedN; i++)
        {
            int i1 = i + 1;
            temp[i] = (uint)(i1 & -i1);
        }

        var permutation = new int[n];
        for (int i = 0; i < n; i++)
        {
            uint rank = code[i] + 1;

            // Find the rank-th unused element via an implicit order-statistics tree.
            int bit = paddedN;
            int next = 0;
            for (int b = 0; b <= log2n; b++)
            {
                int candidate = next + bit;
                bit >>= 1;
                if (temp[candidate - 1] < rank)
                {
                    next = candidate;
                    rank -= temp[candidate - 1];
                }
            }

            permutation[i] = next;

            next++;
            while (next <= paddedN)
            {
                temp[next - 1]--;
                next += next & -next;
            }
        }

        return permutation;
    }
}
