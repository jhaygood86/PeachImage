using System.Numerics;
using System.Runtime.InteropServices;

namespace PeachImage.Formats.Png.Filtering;

/// <summary>
/// SIMD (portable-width <see cref="Vector{T}"/>) implementations of PNG row filtering. Encode-side
/// (<c>Filter*</c>) covers all 5 filter types since none has a cross-byte dependency when encoding — the
/// raw pixel row is already fully known, so a full vector-width chunk can be processed per step.
/// Decode-side (<c>Unfilter*</c>) is different: <see cref="PngFilterType.Up"/> has no same-row dependency
/// at all and is chunk-vectorized the same way encode is. <see cref="PngFilterType.Sub"/> remains scalar
/// (see <see cref="RowFilter"/>'s class doc — issue #34 profiling found it isn't worth the risk in
/// practice). <see cref="PngFilterType.Average"/>/<see cref="PngFilterType.Paeth"/> have a genuine
/// same-row sequential dependency (<c>recon[x]</c> reads the just-reconstructed <c>recon[x-bpp]</c>) and
/// are additionally *nonlinear* recurrences (floor-division, min-distance selection) that don't reduce to
/// a chunk-at-a-time parallel-prefix pattern — <see cref="UnfilterAverage"/>/<see cref="UnfilterPaeth"/>
/// instead vectorize at the per-pixel-step granularity: the sequential step count is unchanged (still one
/// step per pixel), but each step's <c>bpp</c>-byte predictor math runs as one vector op with the previous
/// step's result carried in a register, instead of <c>bpp</c> separate scalar calls — see their own doc
/// comments. A single portable width is used (not a hand-dispatched Vector128/Vector256 pair like Jpeg's
/// DCT/color-conversion kernels) to keep this correctness-critical code simpler to reason about and
/// verify; the JIT still selects the best available hardware width via <see cref="Vector{T}.Count"/>.
/// </summary>
internal static class VectorizedRowFilter
{
    /// <summary>The minimum row length below which vectorizing isn't worth the setup overhead.</summary>
    public static bool IsWorthwhile(int length) => Vector.IsHardwareAccelerated && length >= Vector<byte>.Count * 2;

    public static void FilterSub(ReadOnlySpan<byte> raw, int bpp, Span<byte> destination)
    {
        int n = Vector<byte>.Count;
        int x = 0;

        for (; x < bpp && x < raw.Length; x++)
        {
            destination[x] = raw[x];
        }

        for (; x + n <= raw.Length; x += n)
        {
            var current = Vector.LoadUnsafe(ref MemoryMarshal.GetReference(raw.Slice(x, n)));
            var a = Vector.LoadUnsafe(ref MemoryMarshal.GetReference(raw.Slice(x - bpp, n)));
            (current - a).StoreUnsafe(ref destination[x]);
        }

        for (; x < raw.Length; x++)
        {
            destination[x] = (byte)(raw[x] - raw[x - bpp]);
        }
    }

    public static void FilterUp(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> previousRow, Span<byte> destination)
    {
        int n = Vector<byte>.Count;
        int x = 0;

        for (; x + n <= raw.Length; x += n)
        {
            var current = Vector.LoadUnsafe(ref MemoryMarshal.GetReference(raw.Slice(x, n)));
            var b = Vector.LoadUnsafe(ref MemoryMarshal.GetReference(previousRow.Slice(x, n)));
            (current - b).StoreUnsafe(ref destination[x]);
        }

        for (; x < raw.Length; x++)
        {
            destination[x] = (byte)(raw[x] - previousRow[x]);
        }
    }

    public static void FilterAverage(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> previousRow, int bpp, Span<byte> destination)
    {
        bool hasPrev = !previousRow.IsEmpty;
        int n = Vector<byte>.Count;
        int x = 0;

        for (; x < bpp && x < raw.Length; x++)
        {
            int b = hasPrev ? previousRow[x] : 0;
            destination[x] = (byte)(raw[x] - (b / 2));
        }

        for (; x + n <= raw.Length; x += n)
        {
            var a = Vector.LoadUnsafe(ref MemoryMarshal.GetReference(raw.Slice(x - bpp, n)));
            var b = hasPrev ? Vector.LoadUnsafe(ref MemoryMarshal.GetReference(previousRow.Slice(x, n))) : Vector<byte>.Zero;

            // floor((a+b)/2) without widening or overflow: (a&b) + ((a^b)>>1). Standard unsigned
            // average-without-overflow identity — verified against the scalar (a+b)/2 in tests.
            var avg = (a & b) + Vector.ShiftRightLogical(a ^ b, 1);

            var current = Vector.LoadUnsafe(ref MemoryMarshal.GetReference(raw.Slice(x, n)));
            (current - avg).StoreUnsafe(ref destination[x]);
        }

        for (; x < raw.Length; x++)
        {
            int a = raw[x - bpp];
            int b = hasPrev ? previousRow[x] : 0;
            destination[x] = (byte)(raw[x] - ((a + b) / 2));
        }
    }

    public static void FilterPaeth(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> previousRow, int bpp, Span<byte> destination)
    {
        bool hasPrev = !previousRow.IsEmpty;
        int n = Vector<byte>.Count;
        int x = 0;

        for (; x < bpp && x < raw.Length; x++)
        {
            byte b = hasPrev ? previousRow[x] : (byte)0;
            destination[x] = (byte)(raw[x] - PaethPredictor.Predict(0, b, 0));
        }

        for (; x + n <= raw.Length; x += n)
        {
            // x >= bpp is guaranteed here (the scalar prologue above only exits once x >= bpp).
            var aVec = Vector.LoadUnsafe(ref MemoryMarshal.GetReference(raw.Slice(x - bpp, n)));
            var bVec = hasPrev ? Vector.LoadUnsafe(ref MemoryMarshal.GetReference(previousRow.Slice(x, n))) : Vector<byte>.Zero;
            var cVec = hasPrev ? Vector.LoadUnsafe(ref MemoryMarshal.GetReference(previousRow.Slice(x - bpp, n))) : Vector<byte>.Zero;

            var predictor = PaethVector(aVec, bVec, cVec);

            var current = Vector.LoadUnsafe(ref MemoryMarshal.GetReference(raw.Slice(x, n)));
            (current - predictor).StoreUnsafe(ref destination[x]);
        }

        for (; x < raw.Length; x++)
        {
            byte a = raw[x - bpp];
            byte b = hasPrev ? previousRow[x] : (byte)0;
            byte c = hasPrev ? previousRow[x - bpp] : (byte)0;
            destination[x] = (byte)(raw[x] - PaethPredictor.Predict(a, b, c));
        }
    }

    public static void UnfilterUp(Span<byte> row, ReadOnlySpan<byte> previousRow)
    {
        int n = Vector<byte>.Count;
        int x = 0;

        for (; x + n <= row.Length; x += n)
        {
            var current = Vector.LoadUnsafe(ref row[x]);
            var b = Vector.LoadUnsafe(ref MemoryMarshal.GetReference(previousRow.Slice(x, n)));
            (current + b).StoreUnsafe(ref row[x]);
        }

        for (; x < row.Length; x++)
        {
            row[x] = (byte)(row[x] + previousRow[x]);
        }
    }

    /// <summary>
    /// Decode-side Average unfiltering. Unlike <see cref="FilterAverage"/> (encode), this can't process a
    /// full <see cref="Vector{T}"/>-width chunk per step: <c>recon[x]</c> reads the just-reconstructed
    /// <c>recon[x-bpp]</c>, and floor((a+b)/2) is not affine in <c>a</c> (its rounding depends on <c>a</c>'s
    /// parity), so unlike <see cref="PngFilterType.Sub"/>'s pure running sum, this recurrence has no
    /// closed-form way to combine multiple steps into one vector op ahead of time -- see <see cref="RowFilter"/>'s class doc.
    /// What *is* available: <c>previousRow</c> has no such dependency (it's already fully resolved), so
    /// each pixel (<c>bpp</c> bytes)'s predictor math can be done as one vector op instead of <c>bpp</c>
    /// separate scalar calls, with the just-computed result kept in a register and fed directly into the
    /// next pixel-step as its <c>a</c> input (no reload from memory, no per-byte branch). The sequential
    /// step count is unchanged (still one step per pixel), but replacing <c>bpp</c> scalar iterations'
    /// worth of loop/branch overhead with one vector op per step measured as a real win even at
    /// <c>bpp</c> = 1 (single-channel 8-bit grayscale), not just the larger multi-channel/16-bit cases.
    /// A full <see cref="Vector{T}"/>-width load is used per step (not just <c>bpp</c> bytes) purely to
    /// reuse the same load/store shape as the rest of this file; the lanes beyond <c>bpp</c> are garbage
    /// that's discarded every step (byte-wise and/xor/shift/add have no cross-lane carry, so garbage never
    /// contaminates the low <c>bpp</c> lanes actually stored back).
    /// </summary>
    public static void UnfilterAverage(Span<byte> row, ReadOnlySpan<byte> previousRow, int bpp)
    {
        bool hasPrev = !previousRow.IsEmpty;
        int n = Vector<byte>.Count;

        int x = 0;
        for (; x < bpp && x < row.Length; x++)
        {
            int b = hasPrev ? previousRow[x] : 0;
            row[x] = (byte)(row[x] + (b / 2));
        }

        if (x + n <= row.Length)
        {
            var aVec = Vector.LoadUnsafe(ref row[x - bpp]);

            for (; x + n <= row.Length; x += bpp)
            {
                var bVec = hasPrev ? Vector.LoadUnsafe(ref MemoryMarshal.GetReference(previousRow.Slice(x, n))) : Vector<byte>.Zero;

                // floor((a+b)/2) without widening/overflow, same identity as FilterAverage's encode-side use.
                var avg = (aVec & bVec) + Vector.ShiftRightLogical(aVec ^ bVec, 1);

                var filtVec = Vector.LoadUnsafe(ref row[x]);
                var result = filtVec + avg;

                for (int i = 0; i < bpp; i++)
                {
                    row[x + i] = result[i];
                }

                aVec = result;
            }
        }

        for (; x < row.Length; x++)
        {
            int a = row[x - bpp];
            int b = hasPrev ? previousRow[x] : 0;
            row[x] = (byte)(row[x] + ((a + b) / 2));
        }
    }

    /// <summary>
    /// Decode-side Paeth unfiltering. Same per-pixel-step technique as <see cref="UnfilterAverage"/> (see
    /// its remarks) -- the min-distance predictor selection is even less amenable to a closed-form multi-step
    /// combination than Average's floor-division, so this keeps the same one-step-per-pixel sequential
    /// order and vectorizes only each step's <c>bpp</c>-wide predictor math, reusing <see cref="PaethVector"/>
    /// (the same kernel <see cref="FilterPaeth"/> uses on encode, where all three inputs are already fully
    /// resolved) with the just-reconstructed previous pixel carried in a register as its <c>a</c> input.
    /// </summary>
    public static void UnfilterPaeth(Span<byte> row, ReadOnlySpan<byte> previousRow, int bpp)
    {
        bool hasPrev = !previousRow.IsEmpty;
        int n = Vector<byte>.Count;

        int x = 0;
        for (; x < bpp && x < row.Length; x++)
        {
            byte b = hasPrev ? previousRow[x] : (byte)0;
            row[x] = (byte)(row[x] + PaethPredictor.Predict(0, b, 0));
        }

        if (x + n <= row.Length)
        {
            var aVec = Vector.LoadUnsafe(ref row[x - bpp]);

            for (; x + n <= row.Length; x += bpp)
            {
                var bVec = hasPrev ? Vector.LoadUnsafe(ref MemoryMarshal.GetReference(previousRow.Slice(x, n))) : Vector<byte>.Zero;
                var cVec = hasPrev ? Vector.LoadUnsafe(ref MemoryMarshal.GetReference(previousRow.Slice(x - bpp, n))) : Vector<byte>.Zero;

                var predictor = PaethVector(aVec, bVec, cVec);

                var filtVec = Vector.LoadUnsafe(ref row[x]);
                var result = filtVec + predictor;

                for (int i = 0; i < bpp; i++)
                {
                    row[x + i] = result[i];
                }

                aVec = result;
            }
        }

        for (; x < row.Length; x++)
        {
            byte a = row[x - bpp];
            byte b = hasPrev ? previousRow[x] : (byte)0;
            byte c = hasPrev ? previousRow[x - bpp] : (byte)0;
            row[x] = (byte)(row[x] + PaethPredictor.Predict(a, b, c));
        }
    }

    /// <summary>
    /// Computes the Paeth predictor for a full vector of pixels at once. Widens each byte lane to signed
    /// 16-bit (safe: PNG sample bytes are 0-255, always representable and non-negative as a
    /// <see cref="short"/>, and the predictor's intermediate <c>a+b-c</c> term ranges roughly -255..510,
    /// comfortably within <see cref="short"/>), does the min-distance selection with vectorized
    /// comparisons and <see cref="Vector.ConditionalSelect{T}(Vector{T}, Vector{T}, Vector{T})"/>, then
    /// narrows back to bytes.
    /// </summary>
    private static Vector<byte> PaethVector(Vector<byte> a, Vector<byte> b, Vector<byte> c)
    {
        Vector.Widen(a, out var aLoU, out var aHiU);
        Vector.Widen(b, out var bLoU, out var bHiU);
        Vector.Widen(c, out var cLoU, out var cHiU);

        var predictorLo = PaethPredictorHalf(Vector.AsVectorInt16(aLoU), Vector.AsVectorInt16(bLoU), Vector.AsVectorInt16(cLoU));
        var predictorHi = PaethPredictorHalf(Vector.AsVectorInt16(aHiU), Vector.AsVectorInt16(bHiU), Vector.AsVectorInt16(cHiU));

        return Vector.Narrow(Vector.AsVectorUInt16(predictorLo), Vector.AsVectorUInt16(predictorHi));
    }

    private static Vector<short> PaethPredictorHalf(Vector<short> a, Vector<short> b, Vector<short> c)
    {
        var p = a + b - c;
        var pa = Vector.Abs(p - a);
        var pb = Vector.Abs(p - b);
        var pc = Vector.Abs(p - c);

        var chooseA = Vector.BitwiseAnd(Vector.LessThanOrEqual(pa, pb), Vector.LessThanOrEqual(pa, pc));
        var chooseB = Vector.LessThanOrEqual(pb, pc);

        return Vector.ConditionalSelect(chooseA, a, Vector.ConditionalSelect(chooseB, b, c));
    }
}
