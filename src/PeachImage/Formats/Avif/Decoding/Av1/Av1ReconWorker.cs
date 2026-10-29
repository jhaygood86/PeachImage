using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// Everything needed to predict and reconstruct one transform block, captured by the parse side of
/// <see cref="Av1TileDecoder"/> (which owns all entropy-decoded state) so the pixel side can run later, or on
/// another thread, without touching parser state. Pure data: executing a command is a function of the
/// command, the quantized coefficients it points at, and the pixels already in the planes.
/// </summary>
internal struct Av1ReconCommand
{
    public int Plane;
    public int StartX;
    public int StartY;
    public int TxSz;

    /// <summary>Intra prediction mode (<c>DC_PRED</c> for CFL blocks, as in spec §7.11.2).</summary>
    public int Mode;
    public int AngleDelta;
    public int FilterIntraMode;
    public int CflAlpha;
    public int MaxLumaW;
    public int MaxLumaH;

    /// <summary>Last valid sample coordinates of the plane (<c>maxX - 1</c>, <c>maxY - 1</c>).</summary>
    public int LastX;
    public int LastY;

    public int TxType;
    public int NzRows;
    public int NzCols;
    public int DcQuant;
    public int AcQuant;

    /// <summary>Offset of this block's quantized coefficients in the span handed to <see cref="Av1ReconWorker.Execute"/>.</summary>
    public int CoefOffset;

    /// <summary>False when the prediction was already written to the plane by the parse side (palette / IntraBC blocks).</summary>
    public bool Predict;
    public bool HaveLeft;
    public bool HaveAbove;
    public bool HaveAboveRight;
    public bool HaveBelowLeft;
    public bool FilterTypeSmooth;
    public bool UseFilterIntra;
    public bool IsCfl;
    public bool HasResidual;
    public bool Lossless;
}

/// <summary>
/// The pixel half of tile decoding: intra prediction, chroma-from-luma and residual reconstruction (spec
/// §7.11.2, §7.12.3) for <see cref="Av1ReconCommand"/>s, in the order they were issued. Owns every scratch
/// buffer it uses, so one instance is driven by exactly one thread at a time.
/// </summary>
internal sealed class Av1ReconWorker
{
    private readonly int[][] _planes;
    private readonly int[] _planeWidths;
    private readonly int _bitDepth;
    private readonly bool _enableIntraEdgeFilter;
    private readonly bool _subsamplingX;
    private readonly bool _subsamplingY;

    private readonly int[] _pred = new int[64 * 64];
    private readonly int[] _dequant = new int[64 * 64];
    private readonly int[] _residual = new int[64 * 64];
    private readonly Av1EdgeArray _aboveRow = new(528);
    private readonly Av1EdgeArray _leftCol = new(528);

    public Av1ReconWorker(int[][] planes, int[] planeWidths, Av1SequenceHeader seq)
    {
        _planes = planes;
        _planeWidths = planeWidths;
        _bitDepth = seq.BitDepth;
        _enableIntraEdgeFilter = seq.EnableIntraEdgeFilter;
        _subsamplingX = seq.SubsamplingX;
        _subsamplingY = seq.SubsamplingY;
    }

    /// <summary>Runs one command; <paramref name="quant"/> holds the quantized coefficients its <see cref="Av1ReconCommand.CoefOffset"/> refers to.</summary>
    public void Execute(in Av1ReconCommand c, ReadOnlySpan<int> quant)
    {
        int plane = c.Plane;
        int log2W = Av1TxDimensions.WidthLog2[c.TxSz];
        int log2H = Av1TxDimensions.HeightLog2[c.TxSz];
        int w = 1 << log2W;
        int h = 1 << log2H;
        int stride = _planeWidths[plane];
        var destPlane = _planes[plane];

        if (c.Predict)
        {
            Av1IntraPrediction.BuildEdges(
                _aboveRow, _leftCol,
                destPlane, stride, c.StartX, c.StartY, w, h,
                c.HaveLeft, c.HaveAbove, c.HaveAboveRight, c.HaveBelowLeft, c.LastX, c.LastY, _bitDepth);

            Av1IntraPrediction.Predict(
                _pred, w, h, log2W, log2H, _aboveRow, _leftCol, c.Mode, c.HaveLeft, c.HaveAbove,
                c.UseFilterIntra, c.FilterIntraMode, c.AngleDelta, _enableIntraEdgeFilter,
                c.FilterTypeSmooth, c.LastX, c.LastY, c.StartX, c.StartY, _bitDepth);

            for (int i = 0; i < h; i++)
            {
                _pred.AsSpan(i * w, w).CopyTo(destPlane.AsSpan(((c.StartY + i) * stride) + c.StartX, w));
            }
        }

        if (c.IsCfl)
        {
            int subX = _subsamplingX ? 1 : 0;
            int subY = _subsamplingY ? 1 : 0;
            Av1IntraPrediction.PredictChromaFromLuma(
                destPlane, stride, _planes[0], _planeWidths[0],
                c.StartX, c.StartY, w, h, log2W, log2H, subX, subY, c.CflAlpha, c.MaxLumaW, c.MaxLumaH, _bitDepth);
        }

        if (c.HasResidual)
        {
            Reconstruct(c, quant.Slice(c.CoefOffset), destPlane, stride, w, h);
        }
    }

    /// <summary><c>reconstruct()</c> (spec §7.12.3): dequantize, invoke the 2D inverse transform, and add the (flip-adjusted) residual back into the plane.</summary>
    private void Reconstruct(in Av1ReconCommand c, ReadOnlySpan<int> quant, int[] destPlane, int stride, int w, int h)
    {
        int x = c.StartX;
        int y = c.StartY;
        int txType = c.TxType;

        // No clearing of _dequant is needed: Dequantize writes exactly the extent Inverse2D reads (see the
        // notes on both), so values left over from an earlier block can never surface.
        Av1Dequantizer.Dequantize(quant, _dequant, c.TxSz, c.DcQuant, c.AcQuant, _bitDepth, c.NzRows, c.NzCols);
        Av1InverseTransform.Inverse2D(_dequant, _residual, c.TxSz, txType, c.Lossless, _bitDepth, c.NzRows, c.NzCols);

        bool flipUd = txType is Av1TxType.FlipadstDct or Av1TxType.FlipadstAdst or Av1TxType.VFlipadst or Av1TxType.FlipadstFlipadst;
        bool flipLr = txType is Av1TxType.DctFlipadst or Av1TxType.AdstFlipadst or Av1TxType.HFlipadst or Av1TxType.FlipadstFlipadst;

        int maxSample = (1 << _bitDepth) - 1;

        // The common case (no FLIPADST-involving transform type) reduces to a plain contiguous
        // add-then-clamp per row, vectorizable directly; a flip turns the destination into a
        // reversed-stride write that a single contiguous store can't express.
        if (!flipUd && !flipLr)
        {
            var zeroVec = Vector256<int>.Zero;
            var maxVec = Vector256.Create(maxSample);

            for (int i = 0; i < h; i++)
            {
                int rowBase = ((y + i) * stride) + x;
                int resBase = i * w;
                int j = 0;

                if (Vector256.IsHardwareAccelerated)
                {
                    for (; j + 8 <= w; j += 8)
                    {
                        var pred = Vector256.LoadUnsafe(ref destPlane[rowBase + j]);
                        var res = Vector256.LoadUnsafe(ref _residual[resBase + j]);
                        var clamped = Vector256.Min(Vector256.Max(pred + res, zeroVec), maxVec);
                        clamped.StoreUnsafe(ref destPlane[rowBase + j]);
                    }
                }
                else if (Vector128.IsHardwareAccelerated)
                {
                    var zero128 = Vector128<int>.Zero;
                    var max128 = Vector128.Create(maxSample);
                    for (; j + 4 <= w; j += 4)
                    {
                        var pred = Vector128.LoadUnsafe(ref destPlane[rowBase + j]);
                        var res = Vector128.LoadUnsafe(ref _residual[resBase + j]);
                        var clamped = Vector128.Min(Vector128.Max(pred + res, zero128), max128);
                        clamped.StoreUnsafe(ref destPlane[rowBase + j]);
                    }
                }

                for (; j < w; j++)
                {
                    int idx = rowBase + j;
                    destPlane[idx] = Math.Clamp(destPlane[idx] + _residual[resBase + j], 0, maxSample);
                }
            }

            return;
        }

        for (int i = 0; i < h; i++)
        {
            int yy = flipUd ? h - i - 1 : i;
            for (int j = 0; j < w; j++)
            {
                int xx = flipLr ? w - j - 1 : j;
                int idx = ((y + yy) * stride) + x + xx;
                destPlane[idx] = Math.Clamp(destPlane[idx] + _residual[(i * w) + j], 0, maxSample);
            }
        }
    }
}
