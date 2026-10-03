using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// Quantizes a 4x4 block of forward-DCT coefficients (natural order, as <see cref="Vp8ForwardDct"/> or
/// <see cref="Vp8ForwardWht"/> produce them) into zigzag-scan-order quantized levels, and the reverse
/// (dequantizes zigzag levels back into a natural-order coefficient block) for the encoder's own reconstruction
/// pass. Reuses <see cref="Decoding.Vp8.Vp8QuantMatrix"/>/<see cref="Vp8Dequantizer"/> as-is — quantization is
/// symmetric, so the same per-segment DC/AC step sizes the decoder resolves apply directly to encode.
/// </summary>
internal static class Vp8ForwardQuantizer
{
    /// <summary>
    /// Quantizes <paramref name="coefficients"/> (natural raster order) into <paramref name="quantized"/>
    /// (zigzag scan order, integer levels — not yet multiplied back up by the quant step). Returns the scan
    /// position one past the last nonzero level (0 if the block is entirely zero, 16 if position 15 is
    /// nonzero) — the encode-side mirror of <see cref="Vp8CoefficientDecoder.DecodeBlock"/>'s <c>last</c>
    /// bookkeeping, which the coefficient token encoder needs to know where to stop.
    /// </summary>
    public static int Quantize(ReadOnlySpan<short> coefficients, int dcQuant, int acQuant, Span<short> quantized)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            return QuantizeVector(coefficients, dcQuant, acQuant, quantized);
        }

        return QuantizeScalar(coefficients, dcQuant, acQuant, quantized);
    }

    /// <summary>
    /// <see cref="QuantizeScalar"/> on <see cref="Vector128{T}"/>, with identical results. The integer divide
    /// <c>(|c| + q/2) / q</c> is done in single precision: the numerator is below 2^24, so it and the quotient's
    /// neighbouring integers are all exactly representable, and a correctly rounded float quotient of two such
    /// integers truncates to the exact integer quotient (a non-integer quotient sits at least 1/q away from the
    /// next integer, which is wider than half a float ulp at that magnitude). The zigzag reorder is two
    /// byte-table shuffles over the 16 natural-order levels, and <c>last</c> falls out of a nonzero bit mask.
    /// </summary>
    internal static int QuantizeVector(ReadOnlySpan<short> coefficients, int dcQuant, int acQuant, Span<short> quantized)
    {
        ref short src = ref MemoryMarshal.GetReference(coefficients.Slice(0, 16));
        var ac = Vector128.Create((float)acQuant);
        var acHalf = Vector128.Create(acQuant / 2);

        // Row 0 differs only in lane 0 (the DC coefficient).
        var divisor0 = ac.WithElement(0, dcQuant);
        var half0 = acHalf.WithElement(0, dcQuant / 2);

        var l0 = QuantizeRow(Vector128.LoadUnsafe(ref src, 0), half0, divisor0);
        var l1 = QuantizeRow(Vector128.LoadUnsafe(ref src, 4), acHalf, ac);
        var l2 = QuantizeRow(Vector128.LoadUnsafe(ref src, 8), acHalf, ac);
        var l3 = QuantizeRow(Vector128.LoadUnsafe(ref src, 12), acHalf, ac);

        // Natural-order levels as two vectors of eight shorts; the narrow keeps the low 16 bits, as the (short) cast does.
        var natural0 = Vector128.Narrow(l0, l1);
        var natural1 = Vector128.Narrow(l2, l3);

        var scan0 = Vector128.Shuffle(natural0, ZigZagLow0) | Vector128.Shuffle(natural1, ZigZagLow1);
        var scan1 = Vector128.Shuffle(natural0, ZigZagHigh0) | Vector128.Shuffle(natural1, ZigZagHigh1);

        ref short dst = ref MemoryMarshal.GetReference(quantized.Slice(0, 16));
        scan0.StoreUnsafe(ref dst, 0);
        scan1.StoreUnsafe(ref dst, 8);

        // One bit per scan position, set where the level is nonzero; the highest set bit + 1 is `last`.
        // The 16-bit compares come first (a level can be any multiple of 256), then their all-ones/all-zeros lanes narrow to bytes unchanged.
        var isZero = Vector128.Narrow(Vector128.Equals(scan0, Vector128<short>.Zero), Vector128.Equals(scan1, Vector128<short>.Zero));
        uint nonZero = ~isZero.ExtractMostSignificantBits() & 0xFFFF;
        return 32 - BitOperations.LeadingZeroCount(nonZero);
    }

    private static Vector128<int> QuantizeRow(Vector128<short> coefficients, Vector128<int> half, Vector128<float> divisor)
    {
        var value = Vector128.WidenLower(coefficients);
        var sign = Vector128.ShiftRightArithmetic(value, 31);
        var magnitude = Vector128.Abs(value);
        var level = Vector128.ConvertToInt32(Vector128.ConvertToSingle(magnitude + half) / divisor);

        // Reapply the sign: (level ^ sign) - sign negates exactly where sign is -1.
        return (level ^ sign) - sign;
    }

    internal static int QuantizeScalar(ReadOnlySpan<short> coefficients, int dcQuant, int acQuant, Span<short> quantized)
    {
        int last = 0;
        for (int scan = 0; scan < 16; scan++)
        {
            int naturalPos = Vp8ZigZag.Order[scan];
            int coeff = coefficients[naturalPos];
            int quant = scan == 0 ? dcQuant : acQuant;
            int level = QuantizeOne(coeff, quant);
            quantized[scan] = (short)level;
            if (level != 0)
            {
                last = scan + 1;
            }
        }

        return last;
    }

    /// <summary>
    /// Dequantizes <paramref name="quantized"/> (zigzag scan order levels) back into
    /// <paramref name="output"/> (natural raster order, each level multiplied by its DC/AC quant step) — the
    /// same natural-order/dequantized-value layout <see cref="Vp8CoefficientDecoder.DecodeBlock"/> produces,
    /// so <see cref="Decoding.Vp8.Dct.Vp8ScalarInverseDct"/>/<see cref="Decoding.Vp8.Dct.Vp8ScalarInverseWht"/>
    /// can consume it unchanged for the encoder's own reconstruction pass.
    /// </summary>
    public static void Dequantize(ReadOnlySpan<short> quantized, int dcQuant, int acQuant, Span<short> output)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            DequantizeVector(quantized, dcQuant, acQuant, output);
            return;
        }

        DequantizeScalar(quantized, dcQuant, acQuant, output);
    }

    /// <summary><see cref="DequantizeScalar"/> on vectors: un-zigzag with two-table shuffles, then multiply by the per-position step (16-bit products truncate exactly as the scalar <c>(short)</c> cast does).</summary>
    internal static void DequantizeVector(ReadOnlySpan<short> quantized, int dcQuant, int acQuant, Span<short> output)
    {
        ref short src = ref MemoryMarshal.GetReference(quantized.Slice(0, 16));
        var scan0 = Vector128.LoadUnsafe(ref src, 0);
        var scan1 = Vector128.LoadUnsafe(ref src, 8);

        var natural0 = Vector128.Shuffle(scan0, UnZigZagLow0) | Vector128.Shuffle(scan1, UnZigZagLow1);
        var natural1 = Vector128.Shuffle(scan0, UnZigZagHigh0) | Vector128.Shuffle(scan1, UnZigZagHigh1);

        var acStep = Vector128.Create((short)acQuant);
        ref short dst = ref MemoryMarshal.GetReference(output.Slice(0, 16));
        (natural0 * acStep.WithElement(0, (short)dcQuant)).StoreUnsafe(ref dst, 0);
        (natural1 * acStep).StoreUnsafe(ref dst, 8);
    }

    // Shuffle tables. Vector128.Shuffle yields zero for an out-of-range index, so each output vector is the OR of
    // one shuffle per input vector, with 255 marking "this lane comes from the other input".
    private static readonly Vector128<short> ZigZagLow0 = ShuffleTable(scanBase: 0, fromNaturalHalf: 0, forward: true);
    private static readonly Vector128<short> ZigZagLow1 = ShuffleTable(scanBase: 0, fromNaturalHalf: 1, forward: true);
    private static readonly Vector128<short> ZigZagHigh0 = ShuffleTable(scanBase: 8, fromNaturalHalf: 0, forward: true);
    private static readonly Vector128<short> ZigZagHigh1 = ShuffleTable(scanBase: 8, fromNaturalHalf: 1, forward: true);
    private static readonly Vector128<short> UnZigZagLow0 = ShuffleTable(scanBase: 0, fromNaturalHalf: 0, forward: false);
    private static readonly Vector128<short> UnZigZagLow1 = ShuffleTable(scanBase: 0, fromNaturalHalf: 1, forward: false);
    private static readonly Vector128<short> UnZigZagHigh0 = ShuffleTable(scanBase: 8, fromNaturalHalf: 0, forward: false);
    private static readonly Vector128<short> UnZigZagHigh1 = ShuffleTable(scanBase: 8, fromNaturalHalf: 1, forward: false);

    /// <summary>
    /// Forward (<paramref name="forward"/>): output lane <c>i</c> is scan position <c>scanBase + i</c>, taken from
    /// natural position <c>Order[scan]</c>, which lives in natural vector (<c>pos / 8</c>) lane (<c>pos % 8</c>);
    /// this table serves lanes whose source is natural vector <paramref name="fromNaturalHalf"/>. Inverse: the same
    /// with the roles of scan and natural order swapped (output lane <c>i</c> is natural position
    /// <c>scanBase + i</c>, taken from the scan position whose <c>Order</c> entry equals it).
    /// </summary>
    private static Vector128<short> ShuffleTable(int scanBase, int fromNaturalHalf, bool forward)
    {
        var indices = new short[8];
        for (int lane = 0; lane < 8; lane++)
        {
            int outputPosition = scanBase + lane;
            int source = forward ? Vp8ZigZag.Order[outputPosition] : Array.IndexOf(Vp8ZigZag.Order, outputPosition);
            indices[lane] = source / 8 == fromNaturalHalf ? (short)(source % 8) : (short)255;
        }

        return Vector128.Create(indices);
    }

    internal static void DequantizeScalar(ReadOnlySpan<short> quantized, int dcQuant, int acQuant, Span<short> output)
    {
        output.Clear();
        for (int scan = 0; scan < 16; scan++)
        {
            int level = quantized[scan];
            if (level == 0)
            {
                continue;
            }

            int naturalPos = Vp8ZigZag.Order[scan];
            int quant = scan == 0 ? dcQuant : acQuant;
            output[naturalPos] = (short)(level * quant);
        }
    }

    /// <summary>Round-to-nearest, sign-aware quantization of a single coefficient — magnitude divided by <paramref name="quant"/> with a half-step rounding bias, sign reapplied afterward.</summary>
    private static int QuantizeOne(int coeff, int quant)
    {
        int magnitude = Math.Abs(coeff);
        int level = (magnitude + (quant / 2)) / quant;
        return coeff < 0 ? -level : level;
    }
}
