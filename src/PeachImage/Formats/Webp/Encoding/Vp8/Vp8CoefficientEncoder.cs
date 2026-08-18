using System.Runtime.CompilerServices;
using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// Encodes VP8 DCT coefficient tokens (RFC 6386 section 13) — the write-side mirror of
/// <see cref="Vp8CoefficientDecoder.DecodeBlock"/>'s hand-unrolled cascade, walked in the opposite direction.
/// Reuses <see cref="Vp8CoefficientProbabilities"/>'s flat tables and <see cref="Vp8CoefficientTrees"/>'s
/// band/category tables as-is (the same spec constants serve both directions — only which branch gets written
/// versus read differs), and <see cref="Vp8CoefficientContext"/> unchanged (pure above/left bookkeeping with no
/// read/write asymmetry).
/// </summary>
internal static class Vp8CoefficientEncoder
{
    /// <summary>
    /// Writes one 4x4 block's coefficient tokens from <paramref name="quantized"/> (zigzag scan order, as
    /// <see cref="Vp8ForwardQuantizer.Quantize"/> produces — integer levels, not yet multiplied by the quant
    /// step), stopping once <paramref name="last"/> nonzero coefficients have been written. Mirrors
    /// <see cref="Vp8CoefficientDecoder.DecodeBlock"/>'s parameters exactly (<paramref name="planeType"/>,
    /// <paramref name="firstContext"/>, <paramref name="first"/>) so a caller building the encode-side
    /// equivalent of that method's call sites can pass the same values.
    /// </summary>
    public static void EncodeBlock(
        Vp8BoolEncoder bw,
        byte[] probabilities,
        int planeType,
        int firstContext,
        int first,
        ReadOnlySpan<short> quantized,
        int last)
    {
        int ctx = firstContext;
        int n = first;

        while (n < 16)
        {
            int band = Vp8CoefficientTrees.PositionToBand[n];
            var p = ProbabilitiesFor(probabilities, planeType, band, ctx);

            if (n >= last)
            {
                bw.PutBit(0, p[0]); // End of block: every remaining position is zero.
                return;
            }

            bw.PutBit(1, p[0]); // Not end of block: at least one more nonzero coefficient follows.

            while (quantized[n] == 0)
            {
                bw.PutBit(0, p[1]); // This position is zero; continue the run.
                n++;
                ctx = 0;
                band = Vp8CoefficientTrees.PositionToBand[n];
                p = ProbabilitiesFor(probabilities, planeType, band, ctx);
            }

            bw.PutBit(1, p[1]); // Not zero.

            int magnitude = Math.Abs((int)quantized[n]);
            int nextCtx;
            if (magnitude == 1)
            {
                bw.PutBit(0, p[2]);
                nextCtx = 1;
            }
            else
            {
                bw.PutBit(1, p[2]);
                EncodeLargeValue(bw, p, magnitude);
                nextCtx = 2;
            }

            bw.PutFlag(quantized[n] < 0);

            n++;
            ctx = nextCtx;
        }
    }

    /// <summary>The 11 token-tree node probabilities for one (plane type, band, context) triple — mirrors <see cref="Vp8CoefficientDecoder"/>'s private helper of the same shape.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> ProbabilitiesFor(byte[] probabilities, int planeType, int band, int ctx) =>
        probabilities.AsSpan(
            Vp8CoefficientProbabilities.FlatOffset(planeType, band, ctx),
            Vp8CoefficientProbabilities.NumProbabilities);

    /// <summary>
    /// Writes a magnitude of 2 or more — the write-side mirror of
    /// <see cref="Vp8CoefficientDecoder"/>'s private <c>DecodeLargeValue</c>. Branch boundaries and the
    /// cat3-cat6 base-value formula (<c>3 + (8 &lt;&lt; cat)</c>) are transcribed from that method exactly; the
    /// resulting ranges (11-18, 19-34, 35-66, 67-2114 for cat index 0-3) match RFC 6386's cat3-cat6 token
    /// definitions even though this file's own XML doc comments on <see cref="Vp8CoefficientTrees.Cat3"/> etc.
    /// describe different (each one-category-shifted) ranges — the executable cascade, not those comments, is
    /// what both this encoder and the real decoder actually agree on.
    /// </summary>
    private static void EncodeLargeValue(Vp8BoolEncoder bw, ReadOnlySpan<byte> p, int magnitude)
    {
        if (magnitude < 5)
        {
            bw.PutBit(0, p[3]);
            if (magnitude == 2)
            {
                bw.PutBit(0, p[4]);
            }
            else
            {
                bw.PutBit(1, p[4]);
                bw.PutBit(magnitude - 3, p[5]); // magnitude 3 -> 0, magnitude 4 -> 1.
            }

            return;
        }

        bw.PutBit(1, p[3]);

        if (magnitude < 11)
        {
            bw.PutBit(0, p[6]);
            if (magnitude < 7)
            {
                // cat1: magnitudes 5-6, one extra bit at the fixed probability 159.
                bw.PutBit(0, p[7]);
                bw.PutBit(magnitude - 5, 159);
            }
            else
            {
                // cat2: magnitudes 7-10, two extra bits (MSB first) at the fixed probabilities 165, 145.
                bw.PutBit(1, p[7]);
                int rem = magnitude - 7;
                bw.PutBit((rem >> 1) & 1, 165);
                bw.PutBit(rem & 1, 145);
            }

            return;
        }

        bw.PutBit(1, p[6]);

        int cat;
        byte[] table;
        if (magnitude < 19)
        {
            cat = 0;
            table = Vp8CoefficientTrees.Cat3;
        }
        else if (magnitude < 35)
        {
            cat = 1;
            table = Vp8CoefficientTrees.Cat4;
        }
        else if (magnitude < 67)
        {
            cat = 2;
            table = Vp8CoefficientTrees.Cat5;
        }
        else
        {
            cat = 3;
            table = Vp8CoefficientTrees.Cat6;
        }

        int bit1 = cat >> 1;
        int bit0 = cat & 1;
        bw.PutBit(bit1, p[8]);
        bw.PutBit(bit0, p[9 + bit1]);

        int extra = magnitude - (3 + (8 << cat));
        int numBits = table.Length;
        for (int k = 0; k < numBits; k++)
        {
            int bitPos = numBits - 1 - k;
            int bit = (extra >> bitPos) & 1;
            bw.PutBit(bit, table[k]);
        }
    }
}
