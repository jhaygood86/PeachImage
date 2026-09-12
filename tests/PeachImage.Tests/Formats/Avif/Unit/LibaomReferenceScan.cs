namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Independent, closed-form generator for libaom's own real <c>Default_Scan_*</c> diagonal coefficient
/// scan orders, transcribed from <c>test/scan_test.cc</c>'s own <c>scan_order_test</c>/<c>SCAN_MODE_*</c>
/// definitions (not from <c>av1/common/scan.c</c>'s own hardcoded tables) -- the whole point of this port
/// is that libaom's own real test doesn't compare one hardcoded table against another, it derives the
/// expected scan order from a closed-form mathematical rule (zigzag/row-diagonal/column-diagonal, by
/// shape) and checks the hardcoded table against <em>that</em>. <see cref="Av1ScanTables"/>'s own
/// <c>Default_Scan_*</c> tables are transcribed by hand from the AV1 spec text (its own remarks: "there's
/// no simple closed form... worth risking a transcription-free reimplementation over") -- exactly the kind
/// of ~14-table, hundreds-of-numbers-each transcription a single typo could hide in, and exactly what this
/// algorithmic check is built to catch with zero risk of the check itself being a second, equally
/// fallible transcription.
///
/// <para><b>Row-major convention note</b>: libaom's own real <c>scan_test</c> helper function computes a
/// linear position as <c>c * h + r</c> (column-major, matching AV1's own real coefficient buffer layout
/// convention in that specific helper). <see cref="Av1ScanTables"/>'s own tables were empirically confirmed
/// (by hand-deriving <c>DefaultScan4x4</c>/<c>DefaultScan4x8</c> against both conventions before writing
/// this file) to use <em>row-major</em> positions (<c>r * w + c</c>) instead -- consistent with every other
/// row-major buffer convention already established elsewhere in this codebase (transform/quantize/predict
/// all index <c>(row * width) + col</c>). This generator produces row-major positions to match the real
/// property the stored tables actually satisfy, not the incidental variable-naming/indexing convention of
/// libaom's own C++ helper function.</para>
/// </summary>
internal static class LibaomReferenceScan
{
    /// <summary><c>SCAN_MODE_ZIG_ZAG</c> (<c>scan_test.cc</c>): used for every square (<c>w == h</c>) default scan.</summary>
    public static int[] ZigZag(int w, int h)
    {
        var scan = new int[w * h];
        int dim = w + h - 1;
        int si = 0;

        for (int i = 0; i < dim; i++)
        {
            if (i % 2 == 0)
            {
                for (int c = 0; c < w; c++)
                {
                    int r = i - c;
                    if (r >= 0 && r < h)
                    {
                        scan[si++] = (r * w) + c;
                    }
                }
            }
            else
            {
                for (int r = 0; r < h; r++)
                {
                    int c = i - r;
                    if (c >= 0 && c < w)
                    {
                        scan[si++] = (r * w) + c;
                    }
                }
            }
        }

        return scan;
    }

    /// <summary><c>SCAN_MODE_ROW_DIAG</c> (<c>scan_test.cc</c>): used when <c>h &gt; w</c> (taller than wide).</summary>
    public static int[] RowDiag(int w, int h)
    {
        var scan = new int[w * h];
        int dim = w + h - 1;
        int si = 0;

        for (int i = 0; i < dim; i++)
        {
            for (int r = 0; r < h; r++)
            {
                int c = i - r;
                if (c >= 0 && c < w)
                {
                    scan[si++] = (r * w) + c;
                }
            }
        }

        return scan;
    }

    /// <summary><c>SCAN_MODE_COL_DIAG</c> (<c>scan_test.cc</c>): used when <c>w &gt; h</c> (wider than tall).</summary>
    public static int[] ColDiag(int w, int h)
    {
        var scan = new int[w * h];
        int dim = w + h - 1;
        int si = 0;

        for (int i = 0; i < dim; i++)
        {
            for (int c = 0; c < w; c++)
            {
                int r = i - c;
                if (r >= 0 && r < h)
                {
                    scan[si++] = (r * w) + c;
                }
            }
        }

        return scan;
    }
}
