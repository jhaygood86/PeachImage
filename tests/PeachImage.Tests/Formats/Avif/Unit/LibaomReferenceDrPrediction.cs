namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Direct, independent transcription of libaom's own directional ("angular") intra predictors
/// (<c>av1_dr_prediction_z1_c</c>/<c>z2_c</c>/<c>z3_c</c>, <c>dr_predictor</c>, <c>av1_get_dx</c>/
/// <c>av1_get_dy</c>, all in <c>av1/common/reconintra.c</c>/<c>.h</c>) plus the surrounding
/// edge-filter/upsample orchestration from <c>build_directional_and_filter_intra_predictors</c>'s own
/// <c>is_dr_mode</c> branch (<c>av1/common/reconintra.c:1204-1244</c>), built on
/// <see cref="LibaomReferenceIntraEdge"/>'s own independently-transcribed primitives.
///
/// <para><c>test/dr_prediction_test.cc</c>'s own <c>DrPredTest</c> harness calls the raw
/// <c>av1_dr_prediction_z1/z2/z3_c</c> functions directly with an already-decided
/// <c>upsample_above_</c>/<c>upsample_left_</c> flag (computed once via <c>av1_use_intra_edge_upsample</c>,
/// per <c>RunTest</c>) and a raw, *never actually upsampled or edge-filtered* random/saturated pixel array --
/// it is a SIMD-vs-C bit-exactness check for the tested platform, not a check that the predicted pixels are
/// "correct" against any independent source (there is no other reference for what a real encoded block's
/// neighbor pixels should be). Since this project has no second SIMD implementation to compare against (a
/// libaom-vs-libaom check would be vacuous here), this port instead assembles the *real, full* directional
/// pipeline -- angle computation, edge-filter/corner-filter/upsample selection and application (the real
/// <c>test/intra_edge_test.cc</c> targets), then the z1/z2/z3 core math -- as a single <see cref="Predict"/>
/// entry point, and the paired test (<c>Av1DrPredictionTests</c>) compares its output against
/// <see cref="PeachImage.Formats.Avif.Decoding.Av1.Av1IntraPrediction.Predict"/>'s own directional path
/// (<c>PredictDirectional</c>), which was independently built from the AV1 spec's own §7.11.2.4/§7.11.2.7/
/// §7.11.2.9-§7.11.2.12 text, not from this C source -- so this remains a genuine two-independent-sources
/// check, exercising both real test files' actual target functions together rather than in isolation.</para>
///
/// <para>libaom's own real <c>need_above</c>/<c>need_left</c> gating (angle-derived, not
/// availability-derived: z1's <c>0 &lt; angle &lt;= 90</c> needs only above, z3's <c>180 &lt;= angle &lt; 270</c>
/// needs only left, z2's <c>90 &lt; angle &lt; 180</c> needs both) is reproduced faithfully below even though,
/// for this port's always-both-neighbors-available test scenario, it has no effect on the final predicted
/// pixels: the z1/z2/z3 core math never reads the "unneeded" side's array in the first place, so whether
/// that side was independently filtered/upsampled first is immaterial to <c>dst</c>. It is kept anyway
/// because this is meant to be a faithful transcription of the real algorithm's structure, not merely an
/// output-equivalent simplification of it.</para>
/// </summary>
internal static class LibaomReferenceDrPrediction
{
    private const int AngleStep = 3;

    /// <summary><c>mode_to_angle_map[INTRA_MODES]</c> (<c>av1/common/blockd.h:1150-1152</c>).</summary>
    private static readonly int[] ModeToAngleMap = [0, 90, 180, 45, 135, 113, 157, 203, 67, 0, 0, 0, 0];

    /// <summary><c>dr_intra_derivative[90]</c> (<c>av1/common/reconintra.h:84-116</c>), transcribed directly
    /// from the real C source -- kept as a fresh, independent copy here (rather than reaching into
    /// <c>Av1IntraPrediction</c>'s own private <c>DrIntraDerivative</c> field, which isn't accessible from
    /// this assembly anyway) even though the values themselves are, by construction, identical to a table
    /// that's already known-verbatim-correct.</summary>
    private static readonly int[] DrIntraDerivative =
    [
        0, 0, 0, 1023, 0, 0, 547, 0, 0, 372, 0, 0, 0, 0,
        273, 0, 0, 215, 0, 0, 178, 0, 0, 151, 0, 0, 132, 0, 0,
        116, 0, 0, 102, 0, 0, 0, 90, 0, 0, 80, 0, 0, 71, 0, 0,
        64, 0, 0, 57, 0, 0, 51, 0, 0, 45, 0, 0, 0, 40, 0, 0,
        35, 0, 0, 31, 0, 0, 27, 0, 0, 23, 0, 0, 19, 0, 0,
        15, 0, 0, 0, 0, 11, 0, 0, 7, 0, 0, 3, 0, 0,
    ];

    /// <summary><c>av1_get_dx</c> (<c>av1/common/reconintra.h:118-131</c>).</summary>
    private static int GetDx(int angle)
    {
        if (angle > 0 && angle < 90)
        {
            return DrIntraDerivative[angle];
        }

        if (angle > 90 && angle < 180)
        {
            return DrIntraDerivative[180 - angle];
        }

        return 1;
    }

    /// <summary><c>av1_get_dy</c> (<c>av1/common/reconintra.h:133-146</c>).</summary>
    private static int GetDy(int angle)
    {
        if (angle > 90 && angle < 180)
        {
            return DrIntraDerivative[angle - 90];
        }

        if (angle > 180 && angle < 270)
        {
            return DrIntraDerivative[270 - angle];
        }

        return 1;
    }

    /// <summary>
    /// Full directional-prediction pipeline for one transform block, mirroring
    /// <c>build_directional_and_filter_intra_predictors</c>'s <c>is_dr_mode</c> branch
    /// (<c>av1/common/reconintra.c:1204-1244</c>) with both neighbors always "available" (<c>n_top_px</c>/
    /// <c>n_left_px</c> &gt; 0 unconditionally -- this port's test scenario, like the real
    /// <c>dr_prediction_test.cc</c>'s own harness, always supplies full above/left neighbor data).
    /// <paramref name="above"/>/<paramref name="left"/> are flat arrays with a caller-chosen zero point:
    /// <c>above[aboveOffset + i]</c> is conceptual <c>above_row[i]</c> (so <c>above[aboveOffset - 1]</c> is
    /// the corner sample), and likewise for <paramref name="left"/>/<paramref name="leftOffset"/>. Both must
    /// have room for indices from <c>-2</c> up to at least <c>2 * (w + h) - 2</c> past their offset (the
    /// upsample process's own real read/write extent). <paramref name="mode"/> is an
    /// <see cref="PeachImage.Formats.Avif.Decoding.Av1.Av1IntraMode"/> directional mode value (<c>VPred</c>
    /// through <c>D67Pred</c>). <paramref name="pred"/> is row-major, stride <paramref name="w"/>.
    /// </summary>
    public static void Predict(
        int[] pred,
        int w,
        int h,
        int[] above,
        int aboveOffset,
        int[] left,
        int leftOffset,
        int mode,
        int angleDelta,
        bool enableEdgeFilter,
        bool filterTypeSmooth)
    {
        int pAngle = ModeToAngleMap[mode] + (angleDelta * AngleStep);

        int needAbove;
        int needLeft;
        if (pAngle <= 90)
        {
            needAbove = 1;
            needLeft = 0;
        }
        else if (pAngle < 180)
        {
            needAbove = 1;
            needLeft = 1;
        }
        else
        {
            needAbove = 0;
            needLeft = 1;
        }

        int needRight = pAngle < 90 ? 1 : 0;
        int needBottom = pAngle > 180 ? 1 : 0;

        int upsampleAbove = 0;
        int upsampleLeft = 0;

        if (enableEdgeFilter)
        {
            int filterType = filterTypeSmooth ? 1 : 0;

            if (pAngle != 90 && pAngle != 180)
            {
                if (needAbove == 1 && needLeft == 1 && w + h >= 24)
                {
                    LibaomReferenceIntraEdge.FilterIntraEdgeCorner(above, aboveOffset, left, leftOffset);
                }

                if (needAbove == 1)
                {
                    int strength = LibaomReferenceIntraEdge.IntraEdgeFilterStrength(w, h, pAngle - 90, filterType);
                    int nPx = w + 1 + (needRight == 1 ? h : 0);
                    LibaomReferenceIntraEdge.FilterIntraEdge(above, aboveOffset - 1, nPx, strength);
                }

                if (needLeft == 1)
                {
                    int strength = LibaomReferenceIntraEdge.IntraEdgeFilterStrength(h, w, pAngle - 180, filterType);
                    int nPx = h + 1 + (needBottom == 1 ? w : 0);
                    LibaomReferenceIntraEdge.FilterIntraEdge(left, leftOffset - 1, nPx, strength);
                }
            }

            upsampleAbove = LibaomReferenceIntraEdge.UseIntraEdgeUpsample(w, h, pAngle - 90, filterType) ? 1 : 0;
            if (needAbove == 1 && upsampleAbove == 1)
            {
                int nPx = w + (needRight == 1 ? h : 0);
                LibaomReferenceIntraEdge.UpsampleIntraEdge(above, aboveOffset, nPx);
            }

            upsampleLeft = LibaomReferenceIntraEdge.UseIntraEdgeUpsample(h, w, pAngle - 180, filterType) ? 1 : 0;
            if (needLeft == 1 && upsampleLeft == 1)
            {
                int nPx = h + (needBottom == 1 ? w : 0);
                LibaomReferenceIntraEdge.UpsampleIntraEdge(left, leftOffset, nPx);
            }
        }

        if (pAngle > 0 && pAngle < 90)
        {
            int dx = GetDx(pAngle);
            PredictZ1(pred, w, h, above, aboveOffset, upsampleAbove, dx);
        }
        else if (pAngle > 90 && pAngle < 180)
        {
            int dx = GetDx(pAngle);
            int dy = GetDy(pAngle);
            PredictZ2(pred, w, h, above, aboveOffset, left, leftOffset, upsampleAbove, upsampleLeft, dx, dy);
        }
        else if (pAngle > 180 && pAngle < 270)
        {
            int dy = GetDy(pAngle);
            PredictZ3(pred, w, h, left, leftOffset, upsampleLeft, dy);
        }
        else if (pAngle == 90)
        {
            for (int r = 0; r < h; r++)
            {
                for (int c = 0; c < w; c++)
                {
                    pred[(r * w) + c] = above[aboveOffset + c];
                }
            }
        }
        else
        {
            for (int r = 0; r < h; r++)
            {
                for (int c = 0; c < w; c++)
                {
                    pred[(r * w) + c] = left[leftOffset + r];
                }
            }
        }
    }

    /// <summary><c>av1_dr_prediction_z1_c</c> (<c>av1/common/reconintra.c:532-567</c>), <c>0 &lt; angle &lt; 90</c>.</summary>
    private static void PredictZ1(int[] pred, int bw, int bh, int[] above, int aboveOffset, int upsampleAbove, int dx)
    {
        int maxBaseX = ((bw + bh) - 1) << upsampleAbove;
        int fracBits = 6 - upsampleAbove;
        int baseInc = 1 << upsampleAbove;
        int x = dx;
        for (int r = 0; r < bh; r++, x += dx)
        {
            int baseRow = x >> fracBits;
            int shift = ((x << upsampleAbove) & 0x3F) >> 1;

            if (baseRow >= maxBaseX)
            {
                for (int rr = r; rr < bh; rr++)
                {
                    for (int c = 0; c < bw; c++)
                    {
                        pred[(rr * bw) + c] = above[aboveOffset + maxBaseX];
                    }
                }

                return;
            }

            int b = baseRow;
            for (int c = 0; c < bw; c++, b += baseInc)
            {
                if (b < maxBaseX)
                {
                    int val = (above[aboveOffset + b] * (32 - shift)) + (above[aboveOffset + b + 1] * shift);
                    pred[(r * bw) + c] = Round2(val, 5);
                }
                else
                {
                    pred[(r * bw) + c] = above[aboveOffset + maxBaseX];
                }
            }
        }
    }

    /// <summary><c>av1_dr_prediction_z2_c</c> (<c>av1/common/reconintra.c:570-606</c>), <c>90 &lt; angle &lt; 180</c>.</summary>
    private static void PredictZ2(
        int[] pred,
        int bw,
        int bh,
        int[] above,
        int aboveOffset,
        int[] left,
        int leftOffset,
        int upsampleAbove,
        int upsampleLeft,
        int dx,
        int dy)
    {
        int minBaseX = -(1 << upsampleAbove);
        int fracBitsX = 6 - upsampleAbove;
        int fracBitsY = 6 - upsampleLeft;

        for (int r = 0; r < bh; r++)
        {
            for (int c = 0; c < bw; c++)
            {
                int val;
                int y = r + 1;
                int x = (c << 6) - (y * dx);
                int baseX = x >> fracBitsX;
                if (baseX >= minBaseX)
                {
                    int shift = ((x * (1 << upsampleAbove)) & 0x3F) >> 1;
                    val = (above[aboveOffset + baseX] * (32 - shift)) + (above[aboveOffset + baseX + 1] * shift);
                    val = Round2(val, 5);
                }
                else
                {
                    x = c + 1;
                    y = (r << 6) - (x * dy);
                    int baseY = y >> fracBitsY;
                    int shift = ((y * (1 << upsampleLeft)) & 0x3F) >> 1;
                    val = (left[leftOffset + baseY] * (32 - shift)) + (left[leftOffset + baseY + 1] * shift);
                    val = Round2(val, 5);
                }

                pred[(r * bw) + c] = val;
            }
        }
    }

    /// <summary><c>av1_dr_prediction_z3_c</c> (<c>av1/common/reconintra.c:609-638</c>), <c>180 &lt; angle &lt; 270</c>.</summary>
    private static void PredictZ3(int[] pred, int bw, int bh, int[] left, int leftOffset, int upsampleLeft, int dy)
    {
        int maxBaseY = ((bw + bh) - 1) << upsampleLeft;
        int fracBits = 6 - upsampleLeft;
        int baseInc = 1 << upsampleLeft;
        int y = dy;
        for (int c = 0; c < bw; c++, y += dy)
        {
            int baseCol = y >> fracBits;
            int shift = ((y << upsampleLeft) & 0x3F) >> 1;

            int b = baseCol;
            int r = 0;
            for (; r < bh; r++, b += baseInc)
            {
                if (b < maxBaseY)
                {
                    int val = (left[leftOffset + b] * (32 - shift)) + (left[leftOffset + b + 1] * shift);
                    pred[(r * bw) + c] = Round2(val, 5);
                }
                else
                {
                    break;
                }
            }

            for (; r < bh; r++)
            {
                pred[(r * bw) + c] = left[leftOffset + maxBaseY];
            }
        }
    }

    /// <summary><c>ROUND_POWER_OF_TWO</c> (<c>aom_ports/mem.h</c>).</summary>
    private static int Round2(int value, int n) => (value + (1 << (n - 1))) >> n;
}
