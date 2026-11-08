using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PeachImage.Formats.Webp.Decoding.Vp8.Dct;

namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>
/// Tracks the above/left nonzero-coefficient context (RFC 6386 section 13.4) that feeds each 4x4 block's
/// coefficient decode. "Above" context persists per macroblock column across the whole frame (like a rolling
/// cache of the row immediately above); "left" context is reset at the start of every macroblock row and rolls
/// forward across that row's macroblocks. Y and the Y2/WHT "DC" block track one flag per macroblock column/row
/// position (4 for Y, 1 for DC); chroma (U, V independently) tracks 2, matching their 2x2-subblock layout.
/// </summary>
internal sealed class Vp8CoefficientContext
{
    public readonly bool[] AboveY;
    public readonly bool[] LeftY = new bool[4];
    public readonly bool[] AboveU;
    public readonly bool[] LeftU = new bool[2];
    public readonly bool[] AboveV;
    public readonly bool[] LeftV = new bool[2];

    /// <summary>Nonzero context for the Y2/WHT "DC" block. Only ever touched for non-B_PRED macroblocks - a B_PRED macroblock has no Y2 block and leaves this context exactly as the previous non-B_PRED macroblock left it.</summary>
    public readonly bool[] AboveDc;

    public bool LeftDc;

    public Vp8CoefficientContext(int macroblockCols)
    {
        AboveY = new bool[macroblockCols * 4];
        AboveU = new bool[macroblockCols * 2];
        AboveV = new bool[macroblockCols * 2];
        AboveDc = new bool[macroblockCols];
    }

    public void StartRow()
    {
        Array.Clear(LeftY);
        Array.Clear(LeftU);
        Array.Clear(LeftV);
        LeftDc = false;
    }
}

/// <summary>
/// The decoder's form of the above/left nonzero-coefficient context (RFC 6386 section 13.4) that feeds each 4x4 block's
/// coefficient decode, one bit per block edge (<see cref="Vp8CoefficientContext"/> is the encoder's, a bool per edge). "Above" persists per macroblock column across the whole frame (like a
/// rolling cache of the row immediately above); "left" is reset at the start of every macroblock row and rolls forward
/// across that row's macroblocks. Both use one layout: bits 0-3 are Y (columns for above, rows for left), 4-5 U, 6-7 V,
/// and bit 8 the Y2/WHT "DC" block, which only non-B_PRED macroblocks touch - a B_PRED macroblock leaves that bit exactly
/// as the previous non-B_PRED macroblock left it.
/// </summary>
internal sealed class Vp8TokenContext
{
    /// <summary>The bit for the Y2 block, in both masks.</summary>
    public const uint DcBit = 1u << 8;

    /// <summary>The bits for the Y, U and V blocks, in both masks.</summary>
    public const uint PlaneBits = 0xFF;

    public readonly uint[] Above;

    public uint Left;

    public Vp8TokenContext(int macroblockCols)
    {
        Above = new uint[macroblockCols];
    }

    public void StartRow() => Left = 0;

    /// <summary>Records a macroblock with no coefficients at all: its planes' edges are all zero, and the Y2 edge is too unless the macroblock has no Y2 block.</summary>
    public void ResetSkipped(int mbX, bool isI4x4)
    {
        uint keep = isI4x4 ? DcBit : 0;
        Above[mbX] &= keep;
        Left &= keep;
    }
}

/// <summary>
/// Decodes VP8 DCT coefficient tokens (RFC 6386 section 13) and dequantizes them inline. The token decode
/// itself (not a generic tree walk, but the hand-unrolled cascade libwebp actually ships) is transcribed
/// directly from <c>src/dec/vp8_dec.c</c>'s <c>GetCoeffsFast</c>/<c>GetLargeValue</c>, cross-checked against the
/// downloaded upstream source: <c>p[0]</c> gates end-of-block, <c>p[1]</c> gates a zero coefficient (looping
/// while zero, resetting context to 0 each step), <c>p[2]</c> distinguishes magnitude 1 from "large", and
/// <c>GetLargeValue</c> further resolves magnitudes 2-4, the two-extra-bit "cat1"/"cat2" ranges (5-6, 7-10), and
/// the four table-driven "cat3".."cat6" ranges (19-34, 35-66, 67-130, 131-2114).
/// </summary>
internal static class Vp8CoefficientDecoder
{
    /// <summary>Reads the per-frame coefficient probability table: the default table patched by any bits partition 0 flags as updated (RFC 6386 section 13.4/9.7).</summary>
    /// <remarks>
    /// Returns the flat <c>[planeType][band][context][probability]</c> layout described on
    /// <see cref="Vp8CoefficientProbabilities.DefaultFlat"/>. Because that layout's index is monotonic in
    /// exactly the nesting order the RFC specifies these updates in, the four nested loops this replaced
    /// collapse to one flat walk that is provably the same sequence of <c>GetBit</c>/<c>GetValue</c> calls
    /// against the same probabilities — the bitstream is read identically, byte for byte.
    /// </remarks>
    public static byte[] ParseProbabilityUpdates(Vp8BoolDecoder br)
    {
        var probabilities = new byte[Vp8CoefficientProbabilities.FlatLength];
        Vp8CoefficientProbabilities.DefaultFlat.CopyTo(probabilities, 0);
        var updateProbabilities = Vp8CoefficientProbabilities.UpdateProbabilityFlat;

        for (int i = 0; i < Vp8CoefficientProbabilities.FlatLength; i++)
        {
            if (br.GetBit(updateProbabilities[i]) != 0)
            {
                probabilities[i] = (byte)br.GetValue(8);
            }
        }

        return probabilities;
    }

    /// <summary>
    /// Decodes and dequantizes one macroblock's coefficients into <paramref name="coeffs"/> (25 blocks of 16, already zeroed,
    /// zigzag-descrambled into natural raster order: 0-15 luma, 16-19 U, 20-23 V, 24 the Y2 block), then runs the inverse
    /// Walsh-Hadamard transform that scatters Y2 into each luma block's DC. It also reports per 4x4 block which of the three
    /// reconstruction cases it falls into via two 24-bit masks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bit set in <paramref name="acBlocks"/> means the block carries at least one AC coefficient and needs
    /// the full inverse transform; a bit set in <paramref name="nonZeroBlocks"/> but not in
    /// <paramref name="acBlocks"/> means DC only; clear in both means the block is entirely zero and
    /// reconstruction can skip it outright. At ordinary quality most blocks are in that last case.
    /// </para>
    /// <para>
    /// The AC test is <c>last &gt; 1</c>, where <c>last</c> is the scan position decoding stopped at. That is
    /// exact rather than approximate for two reasons: every coefficient the decoder writes is nonzero (the
    /// magnitude is at least 1 and the quantizer is at least 1), and the zigzag maps scan position 0 to raster
    /// position 0 and every later scan position to a raster position above it - so "stopped past scan 1"
    /// and "some raster position 1..15 is nonzero" are the same statement.
    /// </para>
    /// <para>
    /// <paramref name="probabilities"/> is the flat layout produced by <see cref="ParseProbabilityUpdates"/>; each
    /// (plane type, band, context) triple is a constant-offset window into it, the shape libwebp uses
    /// (<c>const uint8_t* p = prob[n]-&gt;probas_[ctx];</c>). All of a macroblock's blocks go through one copy of the token
    /// walk below, so the bit-reader state stays in registers from the first block to the last instead of
    /// round-tripping through the decoder object between blocks; dequantization, the zigzag and band tables stay out of the
    /// walk for the same reason (with all of that live the JIT spills the reader state to the stack, which puts a
    /// store-forwarding round trip on every bool decode).
    /// </para>
    /// </remarks>
    public static void DecodeMacroblock(
        Vp8BoolDecoder br,
        byte[] probabilities,
        Vp8QuantMatrix quant,
        bool isI4x4,
        ref uint aboveContext,
        ref uint leftContext,
        Span<short> coeffs,
        out uint nonZeroBlocks,
        out uint acBlocks,
        out bool anyNonZero)
    {
        // Every offset below is built from a plane type in [0,3], a band in [0,7] and a context in [0,2];
        // checking the table length once is what lets the per-bit probability reads skip bounds checks.
        if (probabilities.Length != Vp8CoefficientProbabilities.FlatLength || coeffs.Length < 25 * 16)
        {
            throw new ArgumentException("Invalid coefficient decode arguments.");
        }

        ref byte probabilityBase = ref MemoryMarshal.GetArrayDataReference(probabilities);
        ref short coeffBase = ref MemoryMarshal.GetReference(coeffs);
        ReadOnlySpan<byte> aboveBits = BlockAboveBit;
        ReadOnlySpan<byte> leftBits = BlockLeftBit;

        var state = br.Load();
        uint above = aboveContext;
        uint left = leftContext;
        uint ac = 0;
        bool anyTokens = false;
        bool y2Tokens = false;

        // The Y2 block (24) comes first when the macroblock has one, then luma, U and V in order.
        int block = isI4x4 ? 0 : 24;
        while (true)
        {
            int planeType;
            int first;
            int dcQuant;
            int acQuant;
            if (block < 16)
            {
                planeType = isI4x4 ? 3 : 0;
                first = isI4x4 ? 0 : 1;
                dcQuant = quant.Y1Dc;
                acQuant = quant.Y1Ac;
            }
            else if (block < 24)
            {
                planeType = 2;
                first = 0;
                dcQuant = quant.UvDc;
                acQuant = quant.UvAc;
            }
            else
            {
                planeType = 1;
                first = 0;
                dcQuant = quant.Y2Dc;
                acQuant = quant.Y2Ac;
            }

            int aboveBit = aboveBits[block];
            int leftBit = leftBits[block];
            int ctx = (int)(((above >> aboveBit) & 1) + ((left >> leftBit) & 1));
            ref short output = ref Unsafe.Add(ref coeffBase, block * 16);
            ref byte typeBase = ref Unsafe.Add(ref probabilityBase, Vp8CoefficientProbabilities.FlatOffset(planeType, 0, 0));

            int result = WalkTokens(br, ref state, ref typeBase, ctx, first, ref output);

            bool nonZero = result > first;
            if (nonZero)
            {
                Dequantize(ref output, first == 0 ? dcQuant : 1, acQuant);
            }

            above = (above & ~(1u << aboveBit)) | ((nonZero ? 1u : 0u) << aboveBit);
            left = (left & ~(1u << leftBit)) | ((nonZero ? 1u : 0u) << leftBit);

            if (block == 24)
            {
                y2Tokens = nonZero;
                block = 0;
                continue;
            }

            anyTokens |= nonZero;
            ac |= (result > 1 ? 1u : 0u) << block;
            if (++block == 24)
            {
                break;
            }
        }

        br.Store(state);
        aboveContext = above;
        leftContext = left;

        if (!isI4x4)
        {
            Span<short> blockDc = stackalloc short[16];
            Vp8ScalarInverseWht.Transform(coeffs.Slice(24 * 16, 16), blockDc);
            for (int i = 0; i < 16; i++)
            {
                coeffs[i * 16] = blockDc[i];
            }
        }

        // A DC-only block is one whose DC is nonzero (from its own token, or from the Y2 transform) without any AC.
        uint nonZero24 = ac;
        for (int i = 0; i < 24; i++)
        {
            if (Unsafe.Add(ref coeffBase, i * 16) != 0)
            {
                nonZero24 |= 1u << i;
            }
        }

        nonZeroBlocks = nonZero24;
        acBlocks = ac;
        anyNonZero = y2Tokens || anyTokens || nonZero24 != 0;
    }

    /// <summary>
    /// Walks one block's coefficient tokens (RFC 6386 section 13.2) from scan position <paramref name="first"/>, writing the raw
    /// (not yet dequantized) values at their raster positions in <paramref name="output"/>, and returns the scan position it
    /// stopped at: the end-of-block position, or 16. Always inlined, so the caller decides how many copies of the walk exist
    /// and the reader <paramref name="state"/> stays in registers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WalkTokens(
        Vp8BoolDecoder br, ref Vp8BoolDecoder.State state, ref byte typeBase, int ctx, int first, ref short output)
    {
        int n = first;
        int result = 16;
        while (n < 16)
        {
            ref byte p = ref Unsafe.Add(ref typeBase, BandOffsets[n] + (ctx * Vp8CoefficientProbabilities.NumProbabilities));
            if (br.GetBit(ref state, p) == 0)
            {
                result = n;
                break;
            }

            bool reachedEnd = false;
            while (br.GetBit(ref state, Unsafe.Add(ref p, 1)) == 0)
            {
                n++;
                if (n == 16)
                {
                    reachedEnd = true;
                    break;
                }

                p = ref Unsafe.Add(ref typeBase, BandOffsets[n]);
            }

            if (reachedEnd)
            {
                break;
            }

            int magnitude;
            int nextCtx;
            if (br.GetBit(ref state, Unsafe.Add(ref p, 2)) == 0)
            {
                magnitude = 1;
                nextCtx = 1;
            }
            else
            {
                magnitude = DecodeLargeValue(br, ref state, ref p);
                nextCtx = 2;
            }

            // Branch-free sign: a token's sign is a coin flip, so branching on it mispredicts about half the time.
            int signMask = br.GetMask(ref state, 128);
            Unsafe.Add(ref output, ZigZag[n]) = (short)((magnitude ^ signMask) - signMask);

            n++;
            ctx = nextCtx;
        }

        return result;
    }

    /// <summary>
    /// Decodes and dequantizes one 4x4 block's coefficients into <paramref name="output"/> (already zeroed, zigzag-descrambled
    /// into natural raster order). Returns the scan position where decoding stopped (either the end-of-block position, or 16);
    /// the caller derives "this block had any nonzero coefficient" as <c>result &gt; first</c>. The decoder itself goes through
    /// <see cref="DecodeMacroblock"/>; this is the same token walk for a single block, kept as the independent entry point the
    /// encoder's round-trip tests decode through.
    /// </summary>
    internal static int DecodeBlock(
        Vp8BoolDecoder br, byte[] probabilities, int planeType, int firstContext, int first, int dcQuant, int acQuant, Span<short> output)
    {
        if (probabilities.Length != Vp8CoefficientProbabilities.FlatLength || output.Length < 16 || (uint)planeType > 3)
        {
            throw new ArgumentException("Invalid coefficient decode arguments.");
        }

        ref byte typeBase = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(probabilities),
            Vp8CoefficientProbabilities.FlatOffset(planeType, 0, 0));
        ref short origin = ref MemoryMarshal.GetReference(output);

        var state = br.Load();
        int result = WalkTokens(br, ref state, ref typeBase, firstContext, first, ref origin);
        br.Store(state);

        if (result > first)
        {
            Dequantize(ref origin, first == 0 ? dcQuant : 1, acQuant);
        }

        return result;
    }

    /// <summary>Above-edge context bit of each block (0-15 luma, 16-19 U, 20-23 V, 24 Y2), per <see cref="Vp8TokenContext"/>.</summary>
    private static ReadOnlySpan<byte> BlockAboveBit =>
        [0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 4, 5, 4, 5, 6, 7, 6, 7, 8];

    /// <summary>Left-edge context bit of each block, same indexing.</summary>
    private static ReadOnlySpan<byte> BlockLeftBit =>
        [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8];

    /// <summary>
    /// Scales a block's raw token values by their quantizer: <paramref name="dcQuant"/> for raster position 0 and
    /// <paramref name="acQuant"/> for the rest. Truncating each product to 16 bits is what the per-coefficient
    /// <c>(short)(value * quant)</c> this replaces did, and what a 16-bit low multiply does.
    /// </summary>
    private static void Dequantize(ref short origin, int dcQuant, int acQuant)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            var low = Vector128.LoadUnsafe(ref origin);
            var high = Vector128.LoadUnsafe(ref origin, 8);
            var ac = Vector128.Create((short)acQuant);
            var dc = Vector128.WithElement(ac, 0, (short)dcQuant);
            (low * dc).StoreUnsafe(ref origin);
            (high * ac).StoreUnsafe(ref origin, 8);
            return;
        }

        Unsafe.Add(ref origin, 0) = (short)(Unsafe.Add(ref origin, 0) * dcQuant);
        for (int i = 1; i < 16; i++)
        {
            Unsafe.Add(ref origin, i) = (short)(Unsafe.Add(ref origin, i) * acQuant);
        }
    }

    /// <summary>Probability-table offset (band * 33) of each scan position 0..16, i.e. <c>PositionToBand</c> pre-multiplied by the 3 contexts x 11 probabilities per band.</summary>
    private static ReadOnlySpan<byte> BandOffsets =>
    [
        0, 33, 66, 99, 198, 132, 165, 198, 198, 198, 198, 198, 198, 198, 198, 231,
        0,
    ];

    /// <summary>Scan position to raster position, <see cref="Vp8ZigZag.Order"/> as constant data.</summary>
    private static ReadOnlySpan<byte> ZigZag => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeLargeValue(Vp8BoolDecoder br, ref Vp8BoolDecoder.State state, ref byte p)
    {
        int v;
        if (br.GetBit(ref state, Unsafe.Add(ref p, 3)) == 0)
        {
            v = br.GetBit(ref state, Unsafe.Add(ref p, 4)) == 0 ? 2 : 3 - br.GetMask(ref state, Unsafe.Add(ref p, 5));
        }
        else if (br.GetBit(ref state, Unsafe.Add(ref p, 6)) == 0)
        {
            if (br.GetBit(ref state, Unsafe.Add(ref p, 7)) == 0)
            {
                v = 5 - br.GetMask(ref state, 159);
            }
            else
            {
                v = 7 - (2 * br.GetMask(ref state, 165));
                v -= br.GetMask(ref state, 145);
            }
        }
        else
        {
            int bit1 = -br.GetMask(ref state, Unsafe.Add(ref p, 8));
            int bit0 = -br.GetMask(ref state, Unsafe.Add(ref p, 9 + bit1));
            int cat = (2 * bit1) + bit0;
            byte[] table = cat switch
            {
                0 => Vp8CoefficientTrees.Cat3,
                1 => Vp8CoefficientTrees.Cat4,
                2 => Vp8CoefficientTrees.Cat5,
                _ => Vp8CoefficientTrees.Cat6,
            };

            v = 0;
            foreach (byte prob in table)
            {
                v += v - br.GetMask(ref state, prob);
            }

            v += 3 + (8 << cat);
        }

        return v;
    }
}
