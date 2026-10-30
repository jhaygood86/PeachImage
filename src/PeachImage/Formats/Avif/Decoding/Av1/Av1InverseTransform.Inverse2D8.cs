using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeachImage.Formats.Avif.Decoding.Av1;

internal static partial class Av1InverseTransform
{
    [ThreadStatic]
    private static Vector256<int>[]? _rowScratch8;

    [ThreadStatic]
    private static Vector256<int>[]? _colScratch8;

    private static Vector256<int>[] RowScratch8() => _rowScratch8 ??= new Vector256<int>[64];

    private static Vector256<int>[] ColScratch8() => _colScratch8 ??= new Vector256<int>[64];

    /// <summary>Transposes an 8x8 matrix of ints held as eight row vectors. AVX2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8(
        Vector256<int> r0, Vector256<int> r1, Vector256<int> r2, Vector256<int> r3,
        Vector256<int> r4, Vector256<int> r5, Vector256<int> r6, Vector256<int> r7,
        out Vector256<int> o0, out Vector256<int> o1, out Vector256<int> o2, out Vector256<int> o3,
        out Vector256<int> o4, out Vector256<int> o5, out Vector256<int> o6, out Vector256<int> o7)
    {
        Vector256<int> a0 = Avx2.UnpackLow(r0, r1);
        Vector256<int> a1 = Avx2.UnpackHigh(r0, r1);
        Vector256<int> a2 = Avx2.UnpackLow(r2, r3);
        Vector256<int> a3 = Avx2.UnpackHigh(r2, r3);
        Vector256<int> a4 = Avx2.UnpackLow(r4, r5);
        Vector256<int> a5 = Avx2.UnpackHigh(r4, r5);
        Vector256<int> a6 = Avx2.UnpackLow(r6, r7);
        Vector256<int> a7 = Avx2.UnpackHigh(r6, r7);

        Vector256<long> b0 = Avx2.UnpackLow(a0.AsInt64(), a2.AsInt64());
        Vector256<long> b1 = Avx2.UnpackHigh(a0.AsInt64(), a2.AsInt64());
        Vector256<long> b2 = Avx2.UnpackLow(a1.AsInt64(), a3.AsInt64());
        Vector256<long> b3 = Avx2.UnpackHigh(a1.AsInt64(), a3.AsInt64());
        Vector256<long> b4 = Avx2.UnpackLow(a4.AsInt64(), a6.AsInt64());
        Vector256<long> b5 = Avx2.UnpackHigh(a4.AsInt64(), a6.AsInt64());
        Vector256<long> b6 = Avx2.UnpackLow(a5.AsInt64(), a7.AsInt64());
        Vector256<long> b7 = Avx2.UnpackHigh(a5.AsInt64(), a7.AsInt64());

        o0 = Avx2.Permute2x128(b0, b4, 0x20).AsInt32();
        o1 = Avx2.Permute2x128(b1, b5, 0x20).AsInt32();
        o2 = Avx2.Permute2x128(b2, b6, 0x20).AsInt32();
        o3 = Avx2.Permute2x128(b3, b7, 0x20).AsInt32();
        o4 = Avx2.Permute2x128(b0, b4, 0x31).AsInt32();
        o5 = Avx2.Permute2x128(b1, b5, 0x31).AsInt32();
        o6 = Avx2.Permute2x128(b2, b6, 0x31).AsInt32();
        o7 = Avx2.Permute2x128(b3, b7, 0x31).AsInt32();
    }

    /// <summary>Row <paramref name="k"/> of an 8-row batch starting at <paramref name="i"/>: all zero unless it is a real row below <paramref name="rowsLimit"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> LoadBatchRow(int[] dequant, int i, int k, int validRows, int rowsLimit, int col) =>
        k < validRows && i + k < rowsLimit
            ? Vector256.LoadUnsafe(ref dequant[((i + k) * 64) + col])
            : Vector256<int>.Zero;

    /// <summary>
    /// <see cref="Inverse2D"/> on eight-lane <see cref="int"/> vectors: eight rows (or columns) per batch,
    /// 8x8 in-register transposes between the passes, and the residual written straight from registers. See
    /// <c>Av1InverseTransform.Lanes8.cs</c> for the precision contract.
    /// </summary>
    [SkipLocalsInit]
    private static void Inverse2DLanes8(int[] dequant, int[] residual, int txSz, int planeTxType, bool lossless, int bitDepth, int nzRows, int nzCols)
    {
        int log2W = Av1TxDimensions.WidthLog2[txSz];
        int log2H = Av1TxDimensions.HeightLog2[txSz];
        int w = 1 << log2W;
        int h = 1 << log2H;

        int rowShift = lossless ? 0 : TransformRowShift[txSz];
        int colShift = lossless ? 0 : 4;
        int rowClampRange = bitDepth + 8;
        int colClampRange = Math.Max(bitDepth + 6, 16);

        var t = RowScratch8();
        bool rescale = Math.Abs(log2W - log2H) == 1;
        var rescaleFactor = Vector256.Create(2896);

        int rowsLimit = Math.Min(nzRows, 32);
        int colsLimit = Math.Min(nzCols, 32);

        // Row pass, eight rows per batch (h is 4 or a multiple of 8; a 4-row block fills half the lanes).
        for (int i = 0; i < h; i += 8)
        {
            int validRows = Math.Min(8, h - i);
            if (i >= rowsLimit)
            {
                Array.Clear(residual, i * w, validRows * w);
                continue;
            }

            int jl = 0;
            for (; jl + 8 <= colsLimit; jl += 8)
            {
                Transpose8x8(
                    LoadBatchRow(dequant, i, 0, validRows, rowsLimit, jl), LoadBatchRow(dequant, i, 1, validRows, rowsLimit, jl),
                    LoadBatchRow(dequant, i, 2, validRows, rowsLimit, jl), LoadBatchRow(dequant, i, 3, validRows, rowsLimit, jl),
                    LoadBatchRow(dequant, i, 4, validRows, rowsLimit, jl), LoadBatchRow(dequant, i, 5, validRows, rowsLimit, jl),
                    LoadBatchRow(dequant, i, 6, validRows, rowsLimit, jl), LoadBatchRow(dequant, i, 7, validRows, rowsLimit, jl),
                    out t[jl], out t[jl + 1], out t[jl + 2], out t[jl + 3], out t[jl + 4], out t[jl + 5], out t[jl + 6], out t[jl + 7]);
            }

            for (int j = jl; j < colsLimit; j++)
            {
                int v0 = dequant[(i * 64) + j];
                int v1 = 1 < validRows && i + 1 < rowsLimit ? dequant[((i + 1) * 64) + j] : 0;
                int v2 = 2 < validRows && i + 2 < rowsLimit ? dequant[((i + 2) * 64) + j] : 0;
                int v3 = 3 < validRows && i + 3 < rowsLimit ? dequant[((i + 3) * 64) + j] : 0;
                int v4 = 4 < validRows && i + 4 < rowsLimit ? dequant[((i + 4) * 64) + j] : 0;
                int v5 = 5 < validRows && i + 5 < rowsLimit ? dequant[((i + 5) * 64) + j] : 0;
                int v6 = 6 < validRows && i + 6 < rowsLimit ? dequant[((i + 6) * 64) + j] : 0;
                int v7 = 7 < validRows && i + 7 < rowsLimit ? dequant[((i + 7) * 64) + j] : 0;
                t[j] = Vector256.Create(v0, v1, v2, v3, v4, v5, v6, v7);
            }

            for (int j = colsLimit; j < w; j++)
            {
                t[j] = Vector256<int>.Zero;
            }

            if (rescale)
            {
                for (int j = 0; j < w; j++)
                {
                    t[j] = RoundBatch8(t[j] * rescaleFactor, 12);
                }
            }

            if (lossless)
            {
                InverseWhtBatch8(t, 2);
            }
            else if (planeTxType is Av1TxType.DctDct or Av1TxType.AdstDct or Av1TxType.FlipadstDct or Av1TxType.HDct)
            {
                InverseDctBatch8(t, log2W, rowClampRange);
            }
            else if (planeTxType is Av1TxType.DctAdst or Av1TxType.AdstAdst or Av1TxType.DctFlipadst or Av1TxType.FlipadstFlipadst or Av1TxType.AdstFlipadst or Av1TxType.FlipadstAdst or Av1TxType.HAdst or Av1TxType.HFlipadst)
            {
                InverseAdstBatch8(t, log2W, rowClampRange);
            }
            else
            {
                InverseIdentityBatch8(t, log2W);
            }

            if (w >= 8)
            {
                for (int j = 0; j < w; j += 8)
                {
                    Transpose8x8(
                        RoundBatch8(t[j], rowShift), RoundBatch8(t[j + 1], rowShift), RoundBatch8(t[j + 2], rowShift), RoundBatch8(t[j + 3], rowShift),
                        RoundBatch8(t[j + 4], rowShift), RoundBatch8(t[j + 5], rowShift), RoundBatch8(t[j + 6], rowShift), RoundBatch8(t[j + 7], rowShift),
                        out Vector256<int> o0, out Vector256<int> o1, out Vector256<int> o2, out Vector256<int> o3,
                        out Vector256<int> o4, out Vector256<int> o5, out Vector256<int> o6, out Vector256<int> o7);
                    StoreBatchRow(residual, ((i + 0) * w) + j, o0, validRows > 0, 8);
                    StoreBatchRow(residual, ((i + 1) * w) + j, o1, validRows > 1, 8);
                    StoreBatchRow(residual, ((i + 2) * w) + j, o2, validRows > 2, 8);
                    StoreBatchRow(residual, ((i + 3) * w) + j, o3, validRows > 3, 8);
                    StoreBatchRow(residual, ((i + 4) * w) + j, o4, validRows > 4, 8);
                    StoreBatchRow(residual, ((i + 5) * w) + j, o5, validRows > 5, 8);
                    StoreBatchRow(residual, ((i + 6) * w) + j, o6, validRows > 6, 8);
                    StoreBatchRow(residual, ((i + 7) * w) + j, o7, validRows > 7, 8);
                }
            }
            else
            {
                // w == 4: the four column vectors transpose (with four zero vectors) into rows of four.
                var zero = Vector256<int>.Zero;
                Transpose8x8(
                    RoundBatch8(t[0], rowShift), RoundBatch8(t[1], rowShift), RoundBatch8(t[2], rowShift), RoundBatch8(t[3], rowShift),
                    zero, zero, zero, zero,
                    out Vector256<int> o0, out Vector256<int> o1, out Vector256<int> o2, out Vector256<int> o3,
                    out Vector256<int> o4, out Vector256<int> o5, out Vector256<int> o6, out Vector256<int> o7);
                StoreBatchRow(residual, (i + 0) * w, o0, validRows > 0, 4);
                StoreBatchRow(residual, (i + 1) * w, o1, validRows > 1, 4);
                StoreBatchRow(residual, (i + 2) * w, o2, validRows > 2, 4);
                StoreBatchRow(residual, (i + 3) * w, o3, validRows > 3, 4);
                StoreBatchRow(residual, (i + 4) * w, o4, validRows > 4, 4);
                StoreBatchRow(residual, (i + 5) * w, o5, validRows > 5, 4);
                StoreBatchRow(residual, (i + 6) * w, o6, validRows > 6, 4);
                StoreBatchRow(residual, (i + 7) * w, o7, validRows > 7, 4);
            }
        }

        int colBound = 1 << (colClampRange - 1);

        // DC-only DCT_DCT: see the matching block in Inverse2DExact.
        if (!lossless && planeTxType == Av1TxType.DctDct && rowsLimit == 1 && colsLimit == 1)
        {
            bool uniform = true;
            for (int j = 1; j < w; j++)
            {
                uniform &= residual[j] == residual[0];
            }

            if (uniform)
            {
                var tDc = ColScratch8();
                tDc[0] = Vector256.Create(Math.Clamp(residual[0], -colBound, colBound - 1), 0, 0, 0, 0, 0, 0, 0);
                for (int i = 1; i < h; i++)
                {
                    tDc[i] = Vector256<int>.Zero;
                }

                InverseDctBatch8(tDc, log2H, colClampRange);
                for (int i = 0; i < h; i++)
                {
                    int value = RoundBatch8(tDc[i], colShift).GetElement(0);
                    residual.AsSpan(i * w, w).Fill(value);
                }

                return;
            }
        }

        int total = h * w;
        var lo = Vector256.Create(-colBound);
        var hi = Vector256.Create(colBound - 1);
        int k = 0;
        for (; k + 8 <= total; k += 8)
        {
            var v = Vector256.LoadUnsafe(ref residual[k]);
            Vector256.Min(Vector256.Max(v, lo), hi).StoreUnsafe(ref residual[k]);
        }

        for (; k < total; k++)
        {
            residual[k] = Math.Clamp(residual[k], -colBound, colBound - 1);
        }

        // Column pass, eight columns per batch (four for a 4-wide block).
        var tCol = ColScratch8();
        for (int j = 0; j < w; j += 8)
        {
            bool wide = w - j >= 8;
            if (wide)
            {
                for (int i = 0; i < h; i++)
                {
                    tCol[i] = Vector256.LoadUnsafe(ref residual[(i * w) + j]);
                }
            }
            else
            {
                for (int i = 0; i < h; i++)
                {
                    tCol[i] = Vector256.Create(Vector128.LoadUnsafe(ref residual[(i * w) + j]), Vector128<int>.Zero);
                }
            }

            if (lossless)
            {
                InverseWhtBatch8(tCol, 0);
            }
            else if (planeTxType is Av1TxType.DctDct or Av1TxType.DctAdst or Av1TxType.DctFlipadst or Av1TxType.VDct)
            {
                InverseDctBatch8(tCol, log2H, colClampRange);
            }
            else if (planeTxType is Av1TxType.AdstDct or Av1TxType.AdstAdst or Av1TxType.FlipadstDct or Av1TxType.FlipadstFlipadst or Av1TxType.AdstFlipadst or Av1TxType.FlipadstAdst or Av1TxType.VAdst or Av1TxType.VFlipadst)
            {
                InverseAdstBatch8(tCol, log2H, colClampRange);
            }
            else
            {
                InverseIdentityBatch8(tCol, log2H);
            }

            for (int i = 0; i < h; i++)
            {
                var rounded = RoundBatch8(tCol[i], colShift);
                if (wide)
                {
                    rounded.StoreUnsafe(ref residual[(i * w) + j]);
                }
                else
                {
                    rounded.GetLower().StoreUnsafe(ref residual[(i * w) + j]);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBatchRow(int[] residual, int index, Vector256<int> value, bool valid, int count)
    {
        if (!valid)
        {
            return;
        }

        if (count == 8)
        {
            value.StoreUnsafe(ref residual[index]);
        }
        else
        {
            value.GetLower().StoreUnsafe(ref residual[index]);
        }
    }
}
