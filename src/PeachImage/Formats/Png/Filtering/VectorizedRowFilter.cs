using System.Numerics;
using System.Runtime.InteropServices;

namespace PeachImage.Formats.Png.Filtering;

/// <summary>
/// SIMD (portable-width <see cref="Vector{T}"/>) implementations of PNG row filtering. Encode-side
/// (<c>Filter*</c>) covers all 5 filter types since none has a cross-byte dependency when encoding — the
/// raw pixel row is already fully known. Decode-side (<c>Unfilter*</c>) covers only <see cref="PngFilterType.Up"/>:
/// <see cref="PngFilterType.Sub"/>/<see cref="PngFilterType.Average"/>/<see cref="PngFilterType.Paeth"/>
/// defiltering has a genuine same-row sequential dependency (<c>recon[x]</c> reads the just-reconstructed
/// <c>recon[x-bpp]</c>), and Average/Paeth are additionally *nonlinear* recurrences (floor-division,
/// min-distance selection) that don't reduce to a parallel-prefix pattern the way a pure running sum
/// would — see <see cref="RowFilter"/>'s scalar decode path for those. A single portable width is used
/// (not a hand-dispatched Vector128/Vector256 pair like Jpeg's DCT/color-conversion kernels) to keep this
/// correctness-critical code simpler to reason about and verify; the JIT still selects the best available
/// hardware width via <see cref="Vector{T}.Count"/>.
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
