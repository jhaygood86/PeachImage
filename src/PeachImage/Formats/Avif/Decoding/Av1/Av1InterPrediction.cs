namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// IntraBC block-copy prediction: the <c>refIdx == -1</c> (predict from <c>CurrFrame</c> itself) case of the
/// spec's general inter-prediction machinery (§7.11.3.3 "Motion vector scaling process" and §7.11.3.4 "Block
/// inter prediction process"), restricted to what IntraBC actually needs -- single prediction only
/// (<c>isCompound</c> always 0: intrabc never has a second reference), no real reference-frame rescaling
/// (self-reference, so <c>xScale</c>/<c>yScale</c> are always exactly <c>1 &lt;&lt; REF_SCALE_SHIFT</c>), and
/// <c>interp_filter</c> forced to <c>BILINEAR</c> by <c>intra_frame_mode_info()</c>'s <c>use_intrabc</c>
/// branch. Everything else (the two-pass horizontal-then-vertical separable filter, the rounding process,
/// the per-tap phase lookup) is transcribed unchanged from the spec's own general process, not hand-special-
/// cased for the always-integer-luma / occasionally-half-pel-chroma case this restriction produces --
/// running the real two-pass process on the real (mostly zero) BILINEAR taps is simpler to get right than
/// separately proving out a hand-simplified copy-or-average shortcut.
/// </summary>
internal static class Av1InterPrediction
{
    private const int SubpelBits = 4;
    private const int SubpelMask = 15;
    private const int ScaleSubpelBits = 10;

    /// <summary>Subpel_Filters[BILINEAR] (spec §7.11.3.4): the one row of the spec's 6-filter-type table this decoder ever uses, since intrabc always forces interp_filter=BILINEAR.</summary>
    private static readonly int[][] BilinearFilter =
        [
            [0, 0, 0, 128, 0, 0, 0, 0],
            [0, 0, 0, 120, 8, 0, 0, 0],
            [0, 0, 0, 112, 16, 0, 0, 0],
            [0, 0, 0, 104, 24, 0, 0, 0],
            [0, 0, 0, 96, 32, 0, 0, 0],
            [0, 0, 0, 88, 40, 0, 0, 0],
            [0, 0, 0, 80, 48, 0, 0, 0],
            [0, 0, 0, 72, 56, 0, 0, 0],
            [0, 0, 0, 64, 64, 0, 0, 0],
            [0, 0, 0, 56, 72, 0, 0, 0],
            [0, 0, 0, 48, 80, 0, 0, 0],
            [0, 0, 0, 40, 88, 0, 0, 0],
            [0, 0, 0, 32, 96, 0, 0, 0],
            [0, 0, 0, 24, 104, 0, 0, 0],
            [0, 0, 0, 16, 112, 0, 0, 0],
            [0, 0, 0, 8, 120, 0, 0, 0],
        ];

    /// <summary>
    /// Predicts a <paramref name="w"/>x<paramref name="h"/> block at (<paramref name="startX"/>,
    /// <paramref name="startY"/>) in <paramref name="plane"/>'s own (possibly subsampled) pixel grid by
    /// copying/interpolating from the displacement given by <paramref name="deltaRowLuma"/>/
    /// <paramref name="deltaColLuma"/> (the block's <c>Mv</c>, in 1/8th-luma-sample units), writing the
    /// result into <paramref name="pred"/> (row-major, stride <paramref name="w"/>).
    /// </summary>
    public static void PredictIntrabc(
        int[] pred,
        int[] plane,
        int planeStride,
        int startX,
        int startY,
        int w,
        int h,
        int deltaRowLuma,
        int deltaColLuma,
        int subX,
        int subY,
        int lastX,
        int lastY,
        int bitDepth)
    {
        // Motion vector scaling process (spec §7.11.3.3), specialized: xScale = yScale = 1 << REF_SCALE_SHIFT
        // exactly (intrabc always references the current frame at its own size, no superres/scaling), which
        // collapses baseX/baseY down to a pure fixed-point re-expression of (startX<<4)+((2*mv)>>sub) with no
        // scale multiply needed.
        const int halfSample = 1 << (SubpelBits - 1); // 8
        const int off = (1 << (ScaleSubpelBits - SubpelBits)) / 2; // 32

        int origX = (startX << SubpelBits) + ((2 * deltaColLuma) >> subX) + halfSample;
        int origY = (startY << SubpelBits) + ((2 * deltaRowLuma) >> subY) + halfSample;

        int refStartX = ((origX - halfSample) << (ScaleSubpelBits - SubpelBits)) + off;
        int refStartY = ((origY - halfSample) << (ScaleSubpelBits - SubpelBits)) + off;
        const int step = 1 << ScaleSubpelBits; // stepX = stepY = 1024 (no scaling)

        // Block inter prediction process (spec §7.11.3.4), isCompound=0 (intrabc is always single-prediction).
        int interRound0 = bitDepth == 12 ? 5 : 3;
        int interRound1 = bitDepth == 12 ? 9 : 11;

        int intermediateHeight = ((((h - 1) * step) + (1 << ScaleSubpelBits) - 1) >> ScaleSubpelBits) + 8;
        var intermediate = new int[intermediateHeight * w];

        int refBaseRow = refStartY >> ScaleSubpelBits;
        for (int r = 0; r < intermediateHeight; r++)
        {
            int srcRow = Math.Clamp(refBaseRow + r - 3, 0, lastY);
            int srcRowBase = srcRow * planeStride;
            for (int c = 0; c < w; c++)
            {
                int p = refStartX + (step * c);
                int[] taps = BilinearFilter[(p >> 6) & SubpelMask];
                int baseCol = p >> ScaleSubpelBits;
                int s = 0;
                for (int t = 0; t < 8; t++)
                {
                    int srcCol = Math.Clamp(baseCol + t - 3, 0, lastX);
                    s += taps[t] * plane[srcRowBase + srcCol];
                }

                intermediate[(r * w) + c] = Round2(s, interRound0);
            }
        }

        for (int r = 0; r < h; r++)
        {
            int p = (refStartY & 1023) + (step * r);
            int[] taps = BilinearFilter[(p >> 6) & SubpelMask];
            int baseRow = p >> ScaleSubpelBits;
            for (int c = 0; c < w; c++)
            {
                int s = 0;
                for (int t = 0; t < 8; t++)
                {
                    s += taps[t] * intermediate[((baseRow + t) * w) + c];
                }

                pred[(r * w) + c] = Round2(s, interRound1);
            }
        }
    }

    private static int Round2(long x, int n) => n == 0 ? (int)x : (int)((x + (1L << (n - 1))) >> n);
}
