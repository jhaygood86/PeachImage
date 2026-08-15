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
    public static byte[,,,] ParseProbabilityUpdates(Vp8BoolDecoder br)
    {
        var probabilities = (byte[,,,])Vp8CoefficientProbabilities.Default.Clone();

        for (int t = 0; t < Vp8CoefficientProbabilities.NumTypes; t++)
        {
            for (int b = 0; b < Vp8CoefficientProbabilities.NumBands; b++)
            {
                for (int c = 0; c < Vp8CoefficientProbabilities.NumContexts; c++)
                {
                    for (int p = 0; p < Vp8CoefficientProbabilities.NumProbabilities; p++)
                    {
                        if (br.GetBit(Vp8CoefficientProbabilities.UpdateProbability[t, b, c, p]) != 0)
                        {
                            probabilities[t, b, c, p] = (byte)br.GetValue(8);
                        }
                    }
                }
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
    public static int DecodeBlock(
        Vp8BoolDecoder br,
        byte[,,,] probabilities,
        int planeType,
        int firstContext,
        int first,
        int dcQuant,
        int acQuant,
        Span<short> output)
    {
        int ctx = firstContext;
        int n = first;

        while (n < 16)
        {
            int band = Vp8CoefficientTrees.PositionToBand[n];
            if (br.GetBit(probabilities[planeType, band, ctx, 0]) == 0)
            {
                return n;
            }

            while (br.GetBit(probabilities[planeType, band, ctx, 1]) == 0)
            {
                n++;
                if (n == 16)
                {
                    return 16;
                }

                ctx = 0;
                band = Vp8CoefficientTrees.PositionToBand[n];
            }

            int magnitude;
            int nextCtx;
            if (br.GetBit(probabilities[planeType, band, ctx, 2]) == 0)
            {
                magnitude = 1;
                nextCtx = 1;
            }
            else
            {
                magnitude = DecodeLargeValue(br, probabilities, planeType, band, ctx);
                nextCtx = 2;
            }

            int value = br.GetFlag() ? -magnitude : magnitude;
            int quant = n == 0 ? dcQuant : acQuant;
            output[Vp8ZigZag.Order[n]] = (short)(value * quant);

            n++;
            ctx = nextCtx;
        }

        return 16;
    }

    private static int DecodeLargeValue(Vp8BoolDecoder br, byte[,,,] probabilities, int planeType, int band, int ctx)
    {
        byte P(int k) => probabilities[planeType, band, ctx, k];

        int v;
        if (br.GetBit(P(3)) == 0)
        {
            v = br.GetBit(P(4)) == 0 ? 2 : 3 + br.GetBit(P(5));
        }
        else if (br.GetBit(P(6)) == 0)
        {
            if (br.GetBit(P(7)) == 0)
            {
                v = 5 + br.GetBit(159);
            }
            else
            {
                v = 7 + (2 * br.GetBit(165));
                v += br.GetBit(145);
            }
        }
        else
        {
            int bit1 = br.GetBit(P(8));
            int bit0 = br.GetBit(P(9 + bit1));
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
                v += v + br.GetBit(prob);
            }

            v += 3 + (8 << cat);
        }

        return v;
    }
}