using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Encodes one single-tile intra frame: walks the superblock grid (spec §5.11.4 <c>decode_partition()</c>'s
/// write-side mirror), forcing every 64x64 superblock to split all the way down to a uniform 8x8 leaf grid
/// -- this v1 encoder does not implement partition-tree RDO (variable block sizes); every leaf is 8x8, with
/// a real, SSE-cost-based intra mode search (DC/V/H/SMOOTH/PAETH candidates, angle_delta fixed to 0 for the
/// directional candidates) per block. Chroma always uses DC_PRED (guaranteeing <c>ComputeTxType</c> derives
/// <c>DctDct</c> for chroma without needing to search or signal a chroma mode/angle at all -- see
/// <c>Av1TxTypeTables.ModeToTxfm[DC_PRED] == DctDct</c>).
///
/// <para>Requires the luma plane's width/height to already be padded to a multiple
/// of 64 (the caller's job -- see <c>Av1FrameEncoder</c>) so every superblock is a full, in-bounds 64x64
/// block: this eliminates every one of <c>decode_partition()</c>'s edge-of-frame special cases (the
/// <c>hasRows</c>/<c>hasCols</c>-driven HORZ/VERT-forced partitions), which this encoder does not
/// implement.</para>
/// </summary>
internal static class Av1TileEncoder
{
    private static readonly int[] CandidateModes = [Av1IntraMode.DcPred, Av1IntraMode.VPred, Av1IntraMode.HPred, Av1IntraMode.SmoothPred, Av1IntraMode.PaethPred];

    /// <summary>
    /// Encodes the tile and returns its raw byte payload (ready to wrap in a <c>tile_group_obu()</c>).
    /// <paramref name="yPlane"/>/<paramref name="uPlane"/>/<paramref name="vPlane"/> are the true source
    /// planes (already padded); <paramref name="reconY"/>/<paramref name="reconU"/>/<paramref name="reconV"/>
    /// are same-sized output buffers this method fills with the encoder's own local reconstruction (the
    /// same pixels a real decoder will independently reconstruct from this tile's bitstream) -- callers
    /// that only need the encoded bytes may pass fresh same-sized arrays and ignore them.
    /// </summary>
    public static byte[] EncodeTile(
        int[] yPlane, int yWidth, int yHeight,
        int[]? uPlane, int[]? vPlane, int chromaWidth, int chromaHeight,
        int[] reconY, int[]? reconU, int[]? reconV,
        bool monoChrome, int baseQIdx)
    {
        int miCols = yWidth / 4;
        int miRows = yHeight / 4;

        var cdf = new Av1CdfContext(baseQIdx);
        var symbols = new Av1SymbolEncoder(disableCdfUpdate: false);

        var state = new TileState
        {
            SourceY = yPlane,
            SourceU = uPlane,
            SourceV = vPlane,
            ReconY = reconY,
            ReconU = reconU,
            ReconV = reconV,
            YWidth = yWidth,
            YHeight = yHeight,
            ChromaWidth = chromaWidth,
            ChromaHeight = chromaHeight,
            MonoChrome = monoChrome,
            MiCols = miCols,
            MiRows = miRows,
            BaseQIdx = baseQIdx,
            Cdf = cdf,
            Symbols = symbols,
            YModes = new int[miCols * miRows],
            MiSizes = new int[miCols * miRows],
            YCoeffCtx = new Av1CoefficientWriter.PlaneContext(miCols, miRows),
            UCoeffCtx = monoChrome ? null : new Av1CoefficientWriter.PlaneContext(miCols / 2, miRows / 2),
            VCoeffCtx = monoChrome ? null : new Av1CoefficientWriter.PlaneContext(miCols / 2, miRows / 2),
        };

        for (int r = 0; r < miRows; r += 16)
        {
            for (int c = 0; c < miCols; c += 16)
            {
                EncodePartitionForced(state, r, c, sizeMi: 16);
            }
        }

        return symbols.Flush();
    }

    private sealed class TileState
    {
        public required int[] SourceY;
        public required int[]? SourceU;
        public required int[]? SourceV;
        public required int[] ReconY;
        public required int[]? ReconU;
        public required int[]? ReconV;
        public required int YWidth;
        public required int YHeight;
        public required int ChromaWidth;
        public required int ChromaHeight;
        public required bool MonoChrome;
        public required int MiCols;
        public required int MiRows;
        public required int BaseQIdx;
        public required Av1CdfContext Cdf;
        public required Av1SymbolEncoder Symbols;
        public required int[] YModes;
        public required int[] MiSizes;
        public required Av1CoefficientWriter.PlaneContext YCoeffCtx;
        public required Av1CoefficientWriter.PlaneContext? UCoeffCtx;
        public required Av1CoefficientWriter.PlaneContext? VCoeffCtx;
    }

    private static void EncodePartitionForced(TileState s, int r, int c, int sizeMi)
    {
        int bSize = sizeMi switch
        {
            16 => Av1BlockSize.Block64x64,
            8 => Av1BlockSize.Block32x32,
            4 => Av1BlockSize.Block16x16,
            _ => Av1BlockSize.Block8x8,
        };

        // decode_partition() reads a partition symbol at every size down to and including 8x8 (only sizes
        // *below* 8x8 skip it) -- Block8x8 is not exempt, it just always has PARTITION_NONE forced here
        // rather than PARTITION_SPLIT, since our leaf floor is 8x8.
        int ctx = PartitionContext(s, r, c, bSize, out int bsl);
        var partitionCdf = bsl switch
        {
            1 => s.Cdf.PartitionW8[ctx],
            2 => s.Cdf.PartitionW16[ctx],
            3 => s.Cdf.PartitionW32[ctx],
            _ => s.Cdf.PartitionW64[ctx],
        };

        if (sizeMi == 2)
        {
            s.Symbols.WriteSymbol(partitionCdf, Av1PartitionType.None);
            EncodeBlock8x8(s, r, c);
            return;
        }

        s.Symbols.WriteSymbol(partitionCdf, Av1PartitionType.Split);

        int half = sizeMi / 2;
        EncodePartitionForced(s, r, c, half);
        EncodePartitionForced(s, r, c + half, half);
        EncodePartitionForced(s, r + half, c, half);
        EncodePartitionForced(s, r + half, c + half, half);
    }

    private static int PartitionContext(TileState s, int r, int c, int bSize, out int bsl)
    {
        bsl = Av1BlockTables.MiWidthLog2[bSize];
        bool above = r > 0 && Av1BlockTables.MiWidthLog2[s.MiSizes[((r - 1) * s.MiCols) + c]] < bsl;
        bool left = c > 0 && Av1BlockTables.MiHeightLog2[s.MiSizes[(r * s.MiCols) + c - 1]] < bsl;
        return ((left ? 1 : 0) * 2) + (above ? 1 : 0);
    }

    private static void EncodeBlock8x8(TileState s, int r, int c)
    {
        bool availU = r > 0;
        bool availL = c > 0;
        int x = c * 4;
        int y = r * 4;

        // skip: always false, always context 0 (no neighbor ever has skip == true).
        s.Symbols.WriteSymbol(s.Cdf.Skip[0], 0);

        int aboveYMode = availU ? s.YModes[((r - 1) * s.MiCols) + c] : Av1IntraMode.DcPred;
        int leftYMode = availL ? s.YModes[(r * s.MiCols) + c - 1] : Av1IntraMode.DcPred;
        int yModeCtx0 = Av1BlockTables.IntraModeContext[aboveYMode];
        int yModeCtx1 = Av1BlockTables.IntraModeContext[leftYMode];

        var above = new Av1EdgeArray(32);
        var left = new Av1EdgeArray(32);

        // haveAboveRight/haveBelowLeft are conservatively fixed to false: BuildEdges then clamps/replicates
        // its own already-decoded samples instead of reading extended neighbor pixels, which is always safe
        // (never reads not-yet-reconstructed data) and doesn't affect the exact-90/180-degree V_PRED/H_PRED
        // candidates this encoder's fixed angle_delta == 0 restricts itself to.
        Av1IntraPrediction.BuildEdges(above, left, s.ReconY, s.YWidth, x, y, 8, 8, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.YWidth - 1, s.YHeight - 1, bitDepth: 8);

        int bestMode = Av1IntraMode.DcPred;
        long bestCost = long.MaxValue;
        var bestPred = new int[64];
        var pred = new int[64];

        foreach (int mode in CandidateModes)
        {
            Av1IntraPrediction.Predict(pred, 8, 8, 3, 3, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta: 0, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.YWidth - 1, s.YHeight - 1, x, y, bitDepth: 8);

            long sse = 0;
            for (int i = 0; i < 8; i++)
            {
                int rowBase = ((y + i) * s.YWidth) + x;
                for (int j = 0; j < 8; j++)
                {
                    int diff = s.SourceY[rowBase + j] - pred[(i * 8) + j];
                    sse += (long)diff * diff;
                }
            }

            if (sse < bestCost)
            {
                bestCost = sse;
                bestMode = mode;
                Array.Copy(pred, bestPred, pred.Length);
            }
        }

        s.Symbols.WriteSymbol(s.Cdf.IntraFrameYMode[yModeCtx0][yModeCtx1], bestMode);

        if (Av1IntraMode.IsDirectional(bestMode))
        {
            const int maxAngleDelta = 3;
            s.Symbols.WriteSymbol(s.Cdf.AngleDelta[bestMode - Av1IntraMode.VPred], maxAngleDelta);
        }

        bool hasChroma = !s.MonoChrome;
        if (hasChroma)
        {
            // uv_mode is always signalled when hasChroma; always DC_PRED (block width/height == 8 <= 32,
            // so cflAllowed == true -- UvModeCflAllowed is the CDF a real decoder will select too).
            s.Symbols.WriteSymbol(s.Cdf.UvModeCflAllowed[bestMode], Av1IntraMode.DcPred);
        }

        int[] residual = new int[64];
        for (int i = 0; i < 64; i++)
        {
            residual[i] = s.SourceY[((y + (i / 8)) * s.YWidth) + x + (i % 8)] - bestPred[i];
        }

        int[] coeff = new int[64];
        Av1ForwardTransform.Forward2D(residual, coeff, 8);
        int[] levels = new int[64];
        Av1ForwardQuantizer.Quantize(coeff, levels, 8, s.BaseQIdx);

        // Write the prediction into the reconstruction buffer before Reconstruct() adds the residual --
        // matches Av1TileDecoder's own predict-then-reconstruct-in-place ordering.
        for (int i = 0; i < 8; i++)
        {
            Array.Copy(bestPred, i * 8, s.ReconY, ((y + i) * s.YWidth) + x, 8);
        }

        // Y transform type: 8x8 (not >= 32x32) with reduced_tx_set always selects TX_SET_INTRA_2; always
        // signal DCT_DCT, index 1 in TxTypeIntraInvSet2 = [IDTX, DCT_DCT, ADST_ADST, ADST_DCT, DCT_ADST].
        // Only actually written by WriteCoeffs when the block turns out non-all-zero -- see its remarks.
        int txSzSqr = Av1CoeffTables.TxSizeSqr[Av1TxSize.Tx8x8];
        void WriteLumaTxType() => s.Symbols.WriteSymbol(s.Cdf.IntraTxTypeSet2[txSzSqr][bestMode], 1);

        Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 8, ptype: 0, r, c, s.YCoeffCtx, WriteLumaTxType);
        Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, x, y, 8, levels, s.BaseQIdx);

        for (int i = 0; i < 2; i++)
        {
            int idx = ((r + i) * s.MiCols) + c;
            s.YModes[idx] = bestMode;
            s.MiSizes[idx] = Av1BlockSize.Block8x8;
            idx = ((r + i) * s.MiCols) + c + 1;
            s.YModes[idx] = bestMode;
            s.MiSizes[idx] = Av1BlockSize.Block8x8;
        }

        if (hasChroma)
        {
            EncodeChromaBlock(s, r / 2, c / 2, x / 2, y / 2);
        }
    }

    /// <summary>Encodes the one 4x4 chroma (U and V) block corresponding to an 8x8 luma block, always DC_PRED / DCT_DCT.</summary>
    private static void EncodeChromaBlock(TileState s, int chromaR4, int chromaC4, int cx, int cy)
    {
        bool availU = chromaR4 > 0;
        bool availL = chromaC4 > 0;

        foreach (var (source, recon, ctx, ptype) in new[]
        {
            (s.SourceU!, s.ReconU!, s.UCoeffCtx!, 1),
            (s.SourceV!, s.ReconV!, s.VCoeffCtx!, 1),
        })
        {
            var above = new Av1EdgeArray(16);
            var left = new Av1EdgeArray(16);
            Av1IntraPrediction.BuildEdges(above, left, recon, s.ChromaWidth, cx, cy, 4, 4, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.ChromaWidth - 1, s.ChromaHeight - 1, bitDepth: 8);

            var pred = new int[16];
            Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, above, left, Av1IntraMode.DcPred, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta: 0, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.ChromaWidth - 1, s.ChromaHeight - 1, cx, cy, bitDepth: 8);

            var residual = new int[16];
            for (int i = 0; i < 16; i++)
            {
                residual[i] = source[((cy + (i / 4)) * s.ChromaWidth) + cx + (i % 4)] - pred[i];
            }

            var coeff = new int[16];
            Av1ForwardTransform.Forward2D(residual, coeff, 4);
            var levels = new int[16];
            Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

            for (int i = 0; i < 4; i++)
            {
                Array.Copy(pred, i * 4, recon, ((cy + i) * s.ChromaWidth) + cx, 4);
            }

            Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype, chromaR4, chromaC4, ctx);
            Av1LocalReconstructor.Reconstruct(recon, s.ChromaWidth, cx, cy, 4, levels, s.BaseQIdx);
        }
    }
}
