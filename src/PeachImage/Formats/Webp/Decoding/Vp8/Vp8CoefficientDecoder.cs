using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
    /// Decodes and dequantizes one 4x4 block's coefficients into <paramref name="output"/> (already zeroed,
    /// zigzag-descrambled into natural raster order). Returns the scan position where decoding stopped (either
    /// the end-of-block position, or 16); the caller derives "this block had any nonzero coefficient" as
    /// <c>result &gt; first</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="probabilities"/> is the flat layout produced by <see cref="ParseProbabilityUpdates"/>.
    /// Each (plane type, band, context) triple is resolved to an 11-element <see cref="ReadOnlySpan{T}"/> once,
    /// after which every probability read is a constant index into it that RyuJIT can prove in range — the
    /// shape libwebp uses (<c>const uint8_t* p = prob[n]-&gt;probas_[ctx];</c>) and the reason the flat layout
    /// exists at all.
    /// </remarks>
    public static int DecodeBlock(
        Vp8BoolDecoder br,
        byte[] probabilities,
        int planeType,
        int firstContext,
        int first,
        int dcQuant,
        int acQuant,
        Span<short> output)
    {
        // Every offset below is built from a plane type in [0,3], a band in [0,7] and a context in [0,2];
        // checking the table length once is what lets the per-bit probability reads skip bounds checks.
        if (probabilities.Length != Vp8CoefficientProbabilities.FlatLength || output.Length < 16 || (uint)planeType > 3)
        {
            throw new ArgumentException("Invalid coefficient decode arguments.");
        }

        // The loop is deliberately lean: it keeps only the bit-reader state, the position, the context and two
        // references live. Dequantization, the zigzag/band tables and the quantizers stay out of it (tables are
        // constant data, the multiply is one vector op afterward), because with all of that live as well the
        // JIT runs out of registers and spills the bit-reader state to the stack, which puts a store-forwarding
        // round trip on every bool decode.
        ref byte typeBase = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(probabilities),
            Vp8CoefficientProbabilities.FlatOffset(planeType, 0, 0));
        ref short outputBase = ref MemoryMarshal.GetReference(output);
        ReadOnlySpan<byte> bandOffsets = BandOffsets;
        ReadOnlySpan<byte> zigZag = ZigZag;

        var state = br.Load();
        int ctx = firstContext;
        int n = first;
        int result = 16;

        while (n < 16)
        {
            ref byte p = ref Unsafe.Add(ref typeBase, bandOffsets[n] + (ctx * Vp8CoefficientProbabilities.NumProbabilities));
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

                p = ref Unsafe.Add(ref typeBase, bandOffsets[n]);
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

            Unsafe.Add(ref outputBase, zigZag[n]) = (short)(br.GetBit(ref state, 128) != 0 ? -magnitude : magnitude);

            n++;
            ctx = nextCtx;
        }

        br.Store(state);

        if (result > first)
        {
            Dequantize(output, first == 0 ? dcQuant : 1, acQuant);
        }

        return result;
    }

    /// <summary>
    /// Scales a block's raw token values by their quantizer: <paramref name="dcQuant"/> for raster position 0 and
    /// <paramref name="acQuant"/> for the rest. Truncating each product to 16 bits is what the per-coefficient
    /// <c>(short)(value * quant)</c> this replaces did, and what a 16-bit low multiply does.
    /// </summary>
    private static void Dequantize(Span<short> block, int dcQuant, int acQuant)
    {
        ref short origin = ref MemoryMarshal.GetReference(block);

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
            v = br.GetBit(ref state, Unsafe.Add(ref p, 4)) == 0 ? 2 : 3 + br.GetBit(ref state, Unsafe.Add(ref p, 5));
        }
        else if (br.GetBit(ref state, Unsafe.Add(ref p, 6)) == 0)
        {
            if (br.GetBit(ref state, Unsafe.Add(ref p, 7)) == 0)
            {
                v = 5 + br.GetBit(ref state, 159);
            }
            else
            {
                v = 7 + (2 * br.GetBit(ref state, 165));
                v += br.GetBit(ref state, 145);
            }
        }
        else
        {
            int bit1 = br.GetBit(ref state, Unsafe.Add(ref p, 8));
            int bit0 = br.GetBit(ref state, Unsafe.Add(ref p, 9 + bit1));
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
                v += v + br.GetBit(ref state, prob);
            }

            v += 3 + (8 << cat);
        }

        return v;
    }
}
