namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Direct, independent transcription of libaom's own edge-filter/upsample primitives from
/// <c>av1/common/reconintra.c</c> (<c>av1_filter_intra_edge_c</c>, <c>filter_intra_edge_corner</c>,
/// <c>av1_upsample_intra_edge_c</c>, <c>intra_edge_filter_strength</c>) and <c>av1/common/reconintra.h</c>
/// (<c>av1_use_intra_edge_upsample</c>) -- the real reference functions <c>test/intra_edge_test.cc</c>
/// exercises (<c>av1_filter_intra_edge_c</c>/<c>av1_upsample_intra_edge_c</c> directly; the strength/upsample
/// -selection helpers are exercised indirectly through <c>test/dr_prediction_test.cc</c>'s own
/// <c>RunTest</c>, which calls <c>av1_use_intra_edge_upsample</c> to decide its <c>upsample_above_</c>/
/// <c>upsample_left_</c> flags before invoking the z1/z2/z3 predictors). Kept deliberately separate from
/// <see cref="PeachImage.Formats.Avif.Decoding.Av1.Av1IntraPrediction"/>'s own private <c>EdgeFilter</c>/
/// <c>EdgeUpsample</c>/<c>EdgeFilterStrength</c>/<c>EdgeUpsampleSelect</c>/<c>FilterCorner</c>, which were
/// built directly from the AV1 spec's own §7.11.2.9-§7.11.2.12 text, not from this C source -- so a test
/// comparing the two is a genuine two-independent-sources check.
///
/// <para>C's own negative-offset pointer arithmetic (<c>p[-1]</c>, <c>p[-2]</c>) is modeled here with a
/// plain <c>int[]</c> plus an explicit <paramref name="offset"/> parameter on every method: <c>arr[offset + i]</c>
/// is the conceptual <c>p[i]</c>, matching C's own <c>p[i]</c> for whatever base pointer the real call site
/// passes (e.g. <c>av1_filter_intra_edge(above_row - 1, ...)</c> becomes <c>offset: aboveOffset - 1</c> at
/// the call site in <see cref="LibaomReferenceDrPrediction"/>).</para>
/// </summary>
internal static class LibaomReferenceIntraEdge
{
    private const int IntraEdgeTaps = 5;

    /// <summary><c>kernel[INTRA_EDGE_FILT][INTRA_EDGE_TAPS]</c>, local to <c>av1_filter_intra_edge_c</c>.</summary>
    private static readonly int[][] Kernel =
    [
        [0, 4, 8, 4, 0],
        [0, 5, 6, 5, 0],
        [2, 4, 4, 4, 2],
    ];

    /// <summary>
    /// <c>av1_filter_intra_edge_c</c> (<c>av1/common/reconintra.c:1028-1049</c>). Reads/writes
    /// <c>arr[offset .. offset + sz - 1]</c> only (<c>p[0]</c> itself is never modified -- the real loop
    /// starts at <c>i = 1</c>).
    /// </summary>
    public static void FilterIntraEdge(int[] arr, int offset, int sz, int strength)
    {
        if (strength == 0)
        {
            return;
        }

        int filt = strength - 1;
        var edge = new int[sz];
        for (int i = 0; i < sz; i++)
        {
            edge[i] = arr[offset + i];
        }

        for (int i = 1; i < sz; i++)
        {
            int s = 0;
            for (int j = 0; j < IntraEdgeTaps; j++)
            {
                int k = i - 2 + j;
                k = k < 0 ? 0 : k;
                k = k > sz - 1 ? sz - 1 : k;
                s += edge[k] * Kernel[filt][j];
            }

            s = (s + 8) >> 4;
            arr[offset + i] = s;
        }
    }

    /// <summary>
    /// <c>filter_intra_edge_corner</c> (<c>av1/common/reconintra.c:1051-1059</c>). Real signature is
    /// <c>(uint8_t *p_above, uint8_t *p_left)</c>, reading <c>p_left[0]</c>/<c>p_above[-1]</c>/<c>p_above[0]</c>
    /// and writing <c>p_above[-1]</c>/<c>p_left[-1]</c> (the shared corner sample).
    /// </summary>
    public static void FilterIntraEdgeCorner(int[] above, int aboveOffset, int[] left, int leftOffset)
    {
        int s = (left[leftOffset] * 5) + (above[aboveOffset - 1] * 6) + (above[aboveOffset] * 5);
        s = (s + 8) >> 4;
        above[aboveOffset - 1] = s;
        left[leftOffset - 1] = s;
    }

    /// <summary>
    /// <c>av1_upsample_intra_edge_c</c> (<c>av1/common/reconintra.c:1061-1082</c>). Reads
    /// <c>arr[offset - 1 .. offset + sz - 1]</c>, writes <c>arr[offset - 2 .. offset + 2*sz - 2]</c>.
    /// </summary>
    public static void UpsampleIntraEdge(int[] arr, int offset, int sz)
    {
        var input = new int[sz + 3];
        input[0] = arr[offset - 1];
        input[1] = arr[offset - 1];
        for (int i = 0; i < sz; i++)
        {
            input[i + 2] = arr[offset + i];
        }

        input[sz + 2] = arr[offset + sz - 1];

        arr[offset - 2] = input[0];
        for (int i = 0; i < sz; i++)
        {
            int s = -input[i] + (9 * input[i + 1]) + (9 * input[i + 2]) - input[i + 3];
            s = ClipPixel((s + 8) >> 4);
            arr[offset + (2 * i) - 1] = s;
            arr[offset + (2 * i)] = input[i + 2];
        }
    }

    /// <summary>
    /// <c>intra_edge_filter_strength</c> (<c>av1/common/reconintra.c:989-1026</c>). <paramref name="bs0"/>/
    /// <paramref name="bs1"/> mirror the real function's own two size parameters (the caller passes
    /// <c>(txwpx, txhpx)</c> for the above edge and <c>(txhpx, txwpx)</c> for the left edge -- transcribed
    /// as-is even though <c>blk_wh = bs0 + bs1</c> is symmetric either way).
    /// </summary>
    public static int IntraEdgeFilterStrength(int bs0, int bs1, int delta, int type)
    {
        int d = Math.Abs(delta);
        int strength = 0;
        int blkWh = bs0 + bs1;

        if (type == 0)
        {
            if (blkWh <= 8)
            {
                if (d >= 56)
                {
                    strength = 1;
                }
            }
            else if (blkWh <= 12)
            {
                if (d >= 40)
                {
                    strength = 1;
                }
            }
            else if (blkWh <= 16)
            {
                if (d >= 40)
                {
                    strength = 1;
                }
            }
            else if (blkWh <= 24)
            {
                if (d >= 8)
                {
                    strength = 1;
                }

                if (d >= 16)
                {
                    strength = 2;
                }

                if (d >= 32)
                {
                    strength = 3;
                }
            }
            else if (blkWh <= 32)
            {
                if (d >= 1)
                {
                    strength = 1;
                }

                if (d >= 4)
                {
                    strength = 2;
                }

                if (d >= 32)
                {
                    strength = 3;
                }
            }
            else
            {
                if (d >= 1)
                {
                    strength = 3;
                }
            }
        }
        else
        {
            if (blkWh <= 8)
            {
                if (d >= 40)
                {
                    strength = 1;
                }

                if (d >= 64)
                {
                    strength = 2;
                }
            }
            else if (blkWh <= 16)
            {
                if (d >= 20)
                {
                    strength = 1;
                }

                if (d >= 48)
                {
                    strength = 2;
                }
            }
            else if (blkWh <= 24)
            {
                if (d >= 4)
                {
                    strength = 3;
                }
            }
            else
            {
                if (d >= 1)
                {
                    strength = 3;
                }
            }
        }

        return strength;
    }

    /// <summary><c>av1_use_intra_edge_upsample</c> (<c>av1/common/reconintra.h:148-154</c>).</summary>
    public static bool UseIntraEdgeUpsample(int bs0, int bs1, int delta, int type)
    {
        int d = Math.Abs(delta);
        int blkWh = bs0 + bs1;
        if (d == 0 || d >= 40)
        {
            return false;
        }

        return type != 0 ? blkWh <= 8 : blkWh <= 16;
    }

    /// <summary><c>clip_pixel</c> (<c>aom_dsp/aom_dsp_common.h</c>), 8-bit range -- matches the real
    /// <c>av1_upsample_intra_edge_c</c>'s own <see langword="uint8_t"/> output (the highbd variant clips to
    /// the bit depth instead, but this port only exercises the 8-bit path, matching
    /// <see cref="LibaomReferenceDrPrediction"/>'s own 8-bit-only scope).</summary>
    private static int ClipPixel(int val) => val > 255 ? 255 : val < 0 ? 0 : val;
}
