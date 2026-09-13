using System.Buffers;
using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Chooses and applies AV1's loop restoration filters (spec §7.17: Wiener + self-guided/SGR) for a
/// non-lossless frame -- Stage 3 of the two-pass tile-encoder architecture program, real encoder-side search
/// against the already-complete decoder-side filter (<see cref="Av1LoopRestoration"/>), matching this
/// project's own established "reuse the real decoder filter, no second driftable copy" approach already used
/// for CDEF (<see cref="Av1CdefSearch"/>) and deblocking (<see cref="Av1InLoopFilterSearch"/>).
///
/// <para>Runs after both of those (spec's own filter ordering: deblock, then CDEF, then loop restoration
/// last) -- <c>reconY</c>/<c>reconU</c>/<c>reconV</c> already reflect the chosen deblocking level and CDEF
/// strengths by the time this search starts.</para>
///
/// <para><b>Real algorithm, disclosed scope narrowing vs. libaom's own <c>pickrst.c</c></b> (ported from a
/// direct read of the real source, see the master plan's own Stage 3 execution guide): the real per-unit
/// structure -- design a candidate filter per unit, real coordinate-descent hill-climb against genuine SSE
/// (<see cref="Av1LoopRestoration"/>'s own filter math, evaluated locally per unit), a real 2-/3-/4-way
/// Lagrangian RD decision (None vs Wiener vs SGR vs Switchable) using <see cref="Av1RdCost"/>'s existing
/// qindex-derived lambda and real subexponential coefficient-delta bit costs, and a real largest-first
/// unit-size search (256/128/64) with early exit -- is all ported faithfully. Two things are simplified,
/// both disclosed and safe (never worse than "no restoration," never spec-invalid, only possibly leaving
/// some real compression on the table):
/// <list type="bullet">
/// <item><description><b>Wiener filter design starts from the mid-value filter and reaches its final taps
/// purely via hill-climbing</b>, skipping libaom's own alternating-least-squares statistical pre-solve
/// (<c>update_a_sep_sym</c>/<c>update_b_sep_sym</c>/<c>linsolve_wiener</c>) -- a genuinely intricate,
/// fixed-point-overflow-avoidance-heavy piece of code whose entire purpose is giving the hill-climb a
/// better starting point, not changing what the hill-climb itself validates. Since every step is still a
/// real, RD-gated improvement over the previous one (see <see cref="SearchWienerUnit"/>), this can only ever
/// find a *locally* good filter more slowly than libaom would, never an invalid or harmful one.</description></item>
/// <item><description><b>Search-time SSE evaluation reads directly from the post-CDEF reconstruction
/// everywhere</b>, rather than libaom's real stripe-boundary-aware blend of pre-CDEF and post-CDEF samples
/// near each 64-row stripe edge (spec §7.17.1's own reason loop restoration keeps two separate input
/// buffers). This only affects which filter the *search* judges best (a small, localized RD-optimality
/// question near stripe edges); the real final application below still goes through the genuine, stripe-aware
/// <see cref="Av1LoopRestoration.Apply"/> with a real pre-CDEF snapshot, so the actual committed
/// reconstruction -- and what a real decoder reconstructs from the resulting bitstream -- are both spec-exact
/// either way.</description></item>
/// </list>
/// Both narrowings are consistent with this project's own established, disclosed non-byte-identity scope for
/// the lossy path (<c>AvifLibaomEncodeLossyParityBaseline</c>'s own remarks): this is a real, RD-validated,
/// spec-valid search, not an approximation of what "restoration on/off" means.</para>
/// </summary>
internal static class Av1LoopRestorationSearch
{
    // Real libaom's own largest-first search (master plan execution guide §2).
    private static readonly int[] UnitSizeCandidates = [256, 128, 64];

    // Real libaom's own default is exhaustive over all 16 sets (enable_sgr_ep_pruning defaults to off).
    private static readonly int[] SgrSetCandidates = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private static readonly int[] WienerTapsMin = [-5, -23, -17];
    private static readonly int[] WienerTapsMax = [10, 8, 46];
    private static readonly int[] WienerTapsK = [1, 2, 3];
    private static readonly int[] WienerTapsMid = [3, -7, 15];

    private static readonly int[] SgrprojXqdMin = [-96, -32];
    private static readonly int[] SgrprojXqdMax = [31, 95];
    private static readonly int[] SgrprojXqdMid = [-32, 31];

    private const int SgrprojParamsBits = 4;
    private const int SgrprojPrjBits = 7;
    private const int SgrprojPrjSubexpK = 4;
    private const int SgrprojRstBits = 4;
    private const int SgrprojMtableBits = 20;
    private const int SgrprojRecipBits = 12;
    private const int SgrprojSgrBits = 8;
    private const int FilterBits = 7;
    private const int BitDepth = 8;

    /// <summary>
    /// One unit's own real per-hypothesis candidates -- the same real filter design shared across every
    /// frame-level hypothesis that might use it (real libaom's own "reuses the already-computed Wiener/SGR
    /// candidates, no re-solving" structure -- see this type's own class remarks and the master plan's
    /// execution guide, §1).
    /// </summary>
    private readonly struct UnitDesign
    {
        public required long NoneSse { get; init; }
        public required int[] VTaps { get; init; }
        public required int[] HTaps { get; init; }
        public required long WienerSse { get; init; }
        public required int SgrSet { get; init; }
        public required int SgrW0 { get; init; }
        public required int SgrW1 { get; init; }
        public required long SgrSse { get; init; }
    }

    public static Av1LoopRestorationSearchResult SearchAndApply(
        int[] reconY, int[]? reconU, int[]? reconV,
        int[] preCdefY, int[]? preCdefU, int[]? preCdefV,
        int[] sourceY, int[]? sourceU, int[]? sourceV,
        int width, int height, int chromaWidth, int chromaHeight,
        bool monoChrome, int baseQIdx)
    {
        double lambda = Av1RdCost.QIndexToLambda(baseQIdx);
        int planeCount = monoChrome ? 1 : 3;

        int[] bestFrameRestorationType = [Av1LoopRestorationParams.RestoreNone, Av1LoopRestorationParams.RestoreNone, Av1LoopRestorationParams.RestoreNone];
        int[] bestUnitSize = [0, 0, 0];
        Av1RestorationUnitGrid?[] bestGrids = [null, null, null];
        long bestTotalRdCost = long.MaxValue;
        bool anyPlaneUsedRestoration = false;

        foreach (int unitSize in UnitSizeCandidates)
        {
            var frameRestorationType = new int[3];
            var unitSizeAtThisPass = new int[3];
            var grids = new Av1RestorationUnitGrid?[3];
            long totalRdCost = 0;
            bool anyUsed = false;

            for (int plane = 0; plane < planeCount; plane++)
            {
                var (plane0, planeSource, planeWidth, planeHeight) = plane == 0
                    ? (reconY, sourceY, width, height)
                    : plane == 1
                        ? (reconU!, sourceU!, chromaWidth, chromaHeight)
                        : (reconV!, sourceV!, chromaWidth, chromaHeight);

                var planeResult = SearchPlane(plane0, planeSource, planeWidth, planeHeight, unitSize, plane != 0, lambda);
                frameRestorationType[plane] = planeResult.Type;
                unitSizeAtThisPass[plane] = unitSize;
                grids[plane] = planeResult.Grid;
                totalRdCost += planeResult.RdCost;
                if (planeResult.Type != Av1LoopRestorationParams.RestoreNone)
                {
                    anyUsed = true;
                }
            }

            for (int plane = planeCount; plane < 3; plane++)
            {
                frameRestorationType[plane] = Av1LoopRestorationParams.RestoreNone;
                unitSizeAtThisPass[plane] = 0;
            }

            if (totalRdCost < bestTotalRdCost)
            {
                bestTotalRdCost = totalRdCost;
                bestFrameRestorationType = frameRestorationType;
                bestUnitSize = unitSizeAtThisPass;
                bestGrids = grids;
                anyPlaneUsedRestoration = anyUsed;
            }
            else
            {
                // Real libaom's own early exit (pickrst.c:2227-2231): a strictly worse total at a larger
                // unit size means a smaller size will very likely be worse still (more per-unit signaling
                // overhead for the same or less real filtering benefit) -- stop rather than trying every
                // remaining candidate.
                break;
            }

            if (!anyUsed)
            {
                // Every plane already preferred RestoreNone at this size -- a smaller size only adds
                // signaling overhead for content this search has already judged not worth filtering.
                break;
            }
        }

        if (!anyPlaneUsedRestoration)
        {
            return Av1LoopRestorationSearchResult.Off;
        }

        var result = new Av1LoopRestorationSearchResult
        {
            FrameRestorationType = bestFrameRestorationType,
            UsesLr = true,
            UnitSize = bestUnitSize,
            Grids = bestGrids,
        };

        ApplyFinal(result, reconY, reconU, reconV, preCdefY, preCdefU, preCdefV, width, height, chromaWidth, chromaHeight, monoChrome);
        return result;
    }

    /// <summary>Real per-plane search at one candidate <paramref name="unitSize"/>: designs every unit's own real Wiener/SGR candidates, then a real 4-way (or 3-way, single-unit planes never consider Switchable) Lagrangian decision across the whole plane -- master plan execution guide §1/§6.</summary>
    private static (int Type, Av1RestorationUnitGrid? Grid, long RdCost) SearchPlane(int[] reconPlane, int[] sourcePlane, int planeWidth, int planeHeight, int unitSize, bool isChroma, double lambda)
    {
        int unitCols = Av1RestorationUnitGrid.CountUnitsInFrame(unitSize, planeWidth);
        int unitRows = Av1RestorationUnitGrid.CountUnitsInFrame(unitSize, planeHeight);
        int unitCount = unitCols * unitRows;

        var designs = new UnitDesign[unitCount];
        for (int unitRow = 0; unitRow < unitRows; unitRow++)
        {
            for (int unitCol = 0; unitCol < unitCols; unitCol++)
            {
                int unitX = unitCol * unitSize;
                int unitY = unitRow * unitSize;
                int unitW = Math.Min(unitSize, planeWidth - unitX);
                int unitH = Math.Min(unitSize, planeHeight - unitY);
                designs[(unitRow * unitCols) + unitCol] = DesignUnit(reconPlane, sourcePlane, planeWidth, planeHeight, unitX, unitY, unitW, unitH, isChroma);
            }
        }

        // Four real reference chains (master plan execution guide §1): the Wiener-only and Sgrproj-only
        // hypotheses each keep their own running coefficient-delta reference, independent of the switchable
        // hypothesis's own pair -- a real per-unit decision's rate cost (and therefore which type wins)
        // depends on which hypothesis is asking, since each has its own delta-coding history.
        var refWiener = BuildWienerRef();
        var refSgrproj = (int[])SgrprojXqdMid.Clone();
        var switchableRefWiener = BuildWienerRef();
        var switchableRefSgrproj = (int[])SgrprojXqdMid.Clone();

        long noneTotalSse = 0, wienerTotalSse = 0, sgrTotalSse = 0, switchableTotalSse = 0;
        long wienerTotalBits = 0, sgrTotalBits = 0, switchableTotalBits = 0;

        var wienerPerUnitType = new int[unitCount];
        var sgrPerUnitType = new int[unitCount];
        var switchablePerUnitType = new int[unitCount];

        for (int i = 0; i < unitCount; i++)
        {
            var d = designs[i];
            noneTotalSse += d.NoneSse;

            // Wiener-only hypothesis: 2-way (None vs Wiener) type-selector cost.
            long wienerCoeffBits = CountWienerCoeffBits(d.VTaps, d.HTaps, refWiener, isChroma);
            long wienerNoneBits = Av1SymbolEncoder.EstimateSymbolCost(Av1CdfTables.DefaultUseWiener, 0);
            long wienerUseBits = Av1SymbolEncoder.EstimateSymbolCost(Av1CdfTables.DefaultUseWiener, 1) + wienerCoeffBits;
            if (Av1RdCost.CombineCost(d.WienerSse, wienerUseBits, lambda) < Av1RdCost.CombineCost(d.NoneSse, wienerNoneBits, lambda))
            {
                wienerPerUnitType[i] = Av1LoopRestorationParams.RestoreWiener;
                wienerTotalSse += d.WienerSse;
                wienerTotalBits += wienerUseBits;
                UpdateWienerRef(refWiener, d.VTaps, d.HTaps);
            }
            else
            {
                wienerPerUnitType[i] = Av1LoopRestorationParams.RestoreNone;
                wienerTotalSse += d.NoneSse;
                wienerTotalBits += wienerNoneBits;
            }

            // Sgrproj-only hypothesis: 2-way (None vs SGR) type-selector cost.
            long sgrCoeffBits = SgrprojParamsBits + CountSgrCoeffBits(d.SgrSet, d.SgrW0, d.SgrW1, refSgrproj);
            long sgrNoneBits = Av1SymbolEncoder.EstimateSymbolCost(Av1CdfTables.DefaultUseSgrproj, 0);
            long sgrUseBits = Av1SymbolEncoder.EstimateSymbolCost(Av1CdfTables.DefaultUseSgrproj, 1) + sgrCoeffBits;
            if (Av1RdCost.CombineCost(d.SgrSse, sgrUseBits, lambda) < Av1RdCost.CombineCost(d.NoneSse, sgrNoneBits, lambda))
            {
                sgrPerUnitType[i] = Av1LoopRestorationParams.RestoreSgrproj;
                sgrTotalSse += d.SgrSse;
                sgrTotalBits += sgrUseBits;
                UpdateSgrRef(refSgrproj, d.SgrSet, d.SgrW0, d.SgrW1);
            }
            else
            {
                sgrPerUnitType[i] = Av1LoopRestorationParams.RestoreNone;
                sgrTotalSse += d.NoneSse;
                sgrTotalBits += sgrNoneBits;
            }

            // Switchable hypothesis: real 3-way type-selector cost (None/Wiener/Sgrproj), its own reference
            // chain -- real libaom pre-prunes a candidate whose SSE already exceeds RestoreNone's own before
            // even pricing its rate (pickrst.c:1799-1801); a candidate that can never win once any positive
            // rate is added.
            long swWienerCoeffBits = CountWienerCoeffBits(d.VTaps, d.HTaps, switchableRefWiener, isChroma);
            long swSgrCoeffBits = SgrprojParamsBits + CountSgrCoeffBits(d.SgrSet, d.SgrW0, d.SgrW1, switchableRefSgrproj);
            long noneBits = Av1SymbolEncoder.EstimateSymbolCost(Av1CdfTables.DefaultRestorationType, Av1LoopRestorationParams.RestoreNone);
            long swWienerBits = Av1SymbolEncoder.EstimateSymbolCost(Av1CdfTables.DefaultRestorationType, Av1LoopRestorationParams.RestoreWiener) + swWienerCoeffBits;
            long swSgrBits = Av1SymbolEncoder.EstimateSymbolCost(Av1CdfTables.DefaultRestorationType, Av1LoopRestorationParams.RestoreSgrproj) + swSgrCoeffBits;

            long costNone = Av1RdCost.CombineCost(d.NoneSse, noneBits, lambda);
            long costWiener = d.WienerSse <= d.NoneSse ? Av1RdCost.CombineCost(d.WienerSse, swWienerBits, lambda) : long.MaxValue;
            long costSgr = d.SgrSse <= d.NoneSse ? Av1RdCost.CombineCost(d.SgrSse, swSgrBits, lambda) : long.MaxValue;

            if (costWiener <= costNone && costWiener <= costSgr)
            {
                switchablePerUnitType[i] = Av1LoopRestorationParams.RestoreWiener;
                switchableTotalSse += d.WienerSse;
                switchableTotalBits += swWienerBits;
                UpdateWienerRef(switchableRefWiener, d.VTaps, d.HTaps);
            }
            else if (costSgr <= costNone)
            {
                switchablePerUnitType[i] = Av1LoopRestorationParams.RestoreSgrproj;
                switchableTotalSse += d.SgrSse;
                switchableTotalBits += swSgrBits;
                UpdateSgrRef(switchableRefSgrproj, d.SgrSet, d.SgrW0, d.SgrW1);
            }
            else
            {
                switchablePerUnitType[i] = Av1LoopRestorationParams.RestoreNone;
                switchableTotalSse += d.NoneSse;
                switchableTotalBits += noneBits;
            }
        }

        long rdNone = Av1RdCost.CombineCost(noneTotalSse, 0, lambda);
        long rdWiener = Av1RdCost.CombineCost(wienerTotalSse, wienerTotalBits, lambda);
        long rdSgr = Av1RdCost.CombineCost(sgrTotalSse, sgrTotalBits, lambda);
        long rdSwitchable = unitCount > 1 ? Av1RdCost.CombineCost(switchableTotalSse, switchableTotalBits, lambda) : long.MaxValue;

        long best = Math.Min(Math.Min(rdNone, rdWiener), Math.Min(rdSgr, rdSwitchable));

        if (best == rdNone)
        {
            return (Av1LoopRestorationParams.RestoreNone, null, rdNone);
        }

        int winningType = best == rdWiener ? Av1LoopRestorationParams.RestoreWiener
            : best == rdSgr ? Av1LoopRestorationParams.RestoreSgrproj
            : Av1LoopRestorationParams.RestoreSwitchable;
        var perUnitType = winningType == Av1LoopRestorationParams.RestoreWiener ? wienerPerUnitType
            : winningType == Av1LoopRestorationParams.RestoreSgrproj ? sgrPerUnitType
            : switchablePerUnitType;

        var grid = BuildGrid(designs, perUnitType, unitRows, unitCols, unitSize);
        return (winningType, grid, best);
    }

    private static Av1RestorationUnitGrid BuildGrid(UnitDesign[] designs, int[] perUnitType, int unitRows, int unitCols, int unitSize)
    {
        int unitCount = designs.Length;
        var lrType = new int[unitCount];
        var lrWiener = new int[unitCount * 2 * 3];
        var lrSgrSet = new int[unitCount];
        var lrSgrXqd = new int[unitCount * 2];

        for (int i = 0; i < unitCount; i++)
        {
            lrType[i] = perUnitType[i];
            var d = designs[i];
            if (perUnitType[i] == Av1LoopRestorationParams.RestoreWiener)
            {
                for (int j = 0; j < 3; j++)
                {
                    lrWiener[((i * 2) * 3) + j] = d.VTaps[j];
                    lrWiener[(((i * 2) + 1) * 3) + j] = d.HTaps[j];
                }
            }
            else if (perUnitType[i] == Av1LoopRestorationParams.RestoreSgrproj)
            {
                lrSgrSet[i] = d.SgrSet;
                lrSgrXqd[(i * 2) + 0] = d.SgrW0;
                lrSgrXqd[(i * 2) + 1] = d.SgrW1;
            }
        }

        return new Av1RestorationUnitGrid
        {
            UnitRows = unitRows,
            UnitCols = unitCols,
            UnitSize = unitSize,
            LrType = lrType,
            LrWiener = lrWiener,
            LrSgrSet = lrSgrSet,
            LrSgrXqd = lrSgrXqd,
        };
    }

    /// <summary>Real per-unit filter design (master plan execution guide §3/§4): the real Wiener/SGR candidates every frame-level hypothesis reuses, per this type's own class remarks on why the ALS pre-solve is skipped in favor of hill-climbing straight from the mid-value filter.</summary>
    private static UnitDesign DesignUnit(int[] reconPlane, int[] sourcePlane, int planeWidth, int planeHeight, int unitX, int unitY, int unitW, int unitH, bool isChroma)
    {
        long noneSse = 0;
        for (int y = 0; y < unitH; y++)
        {
            int rowBase = (unitY + y) * planeWidth;
            for (int x = 0; x < unitW; x++)
            {
                long diff = reconPlane[rowBase + unitX + x] - sourcePlane[rowBase + unitX + x];
                noneSse += diff * diff;
            }
        }

        var (vTaps, hTaps, wienerSse) = SearchWienerUnit(reconPlane, sourcePlane, planeWidth, planeHeight, unitX, unitY, unitW, unitH, isChroma);
        var (sgrSet, sgrW0, sgrW1, sgrSse) = SearchSgrUnit(reconPlane, sourcePlane, planeWidth, planeHeight, unitX, unitY, unitW, unitH);

        return new UnitDesign
        {
            NoneSse = noneSse,
            VTaps = vTaps,
            HTaps = hTaps,
            WienerSse = wienerSse,
            SgrSet = sgrSet,
            SgrW0 = sgrW0,
            SgrW1 = sgrW1,
            SgrSse = sgrSse,
        };
    }

    // ---- Wiener: hill-climb from the mid-value filter, real SSE via the real (locally edge-clamped) 2-pass filter math ----

    private static (int[] VTaps, int[] HTaps, long Sse) SearchWienerUnit(int[] reconPlane, int[] sourcePlane, int planeWidth, int planeHeight, int unitX, int unitY, int unitW, int unitH, bool isChroma)
    {
        int firstIdx = isChroma ? 1 : 0;
        var vTaps = (int[])WienerTapsMid.Clone();
        var hTaps = (int[])WienerTapsMid.Clone();
        if (isChroma)
        {
            vTaps[0] = 0;
            hTaps[0] = 0;
        }

        // Preallocated once per unit and reused across every hill-climb evaluation below (potentially
        // hundreds per unit) -- allocating a fresh 2D array per evaluation was the real, dominant cost of
        // this search (GC pressure from a tight hot loop, not raw arithmetic; confirmed by direct timing
        // during this stage's own verification -- a single 384x384 lossy image took nearly a minute before
        // this fix, vs. a fraction of a second for equivalent-sized content once buffers were reused).
        var vFilter = new int[7];
        var hFilter = new int[7];
        var intermediate = new int[unitH + 6, unitW];

        long bestSse = EvaluateWienerSse(reconPlane, sourcePlane, planeWidth, planeHeight, unitX, unitY, unitW, unitH, vTaps, hTaps, isChroma, vFilter, hFilter, intermediate);

        foreach (var taps in new[] { hTaps, vTaps })
        {
            for (int j = firstIdx; j < 3; j++)
            {
                for (int step = 4; step >= 1; step >>= 1)
                {
                    bool improved = true;
                    while (improved)
                    {
                        improved = false;
                        int orig = taps[j];
                        foreach (int dir in stackalloc[] { step, -step })
                        {
                            int candidate = Math.Clamp(orig + dir, WienerTapsMin[j], WienerTapsMax[j]);
                            if (candidate == taps[j])
                            {
                                continue;
                            }

                            int prev = taps[j];
                            taps[j] = candidate;
                            long sse = EvaluateWienerSse(reconPlane, sourcePlane, planeWidth, planeHeight, unitX, unitY, unitW, unitH, vTaps, hTaps, isChroma, vFilter, hFilter, intermediate);
                            if (sse < bestSse)
                            {
                                bestSse = sse;
                                improved = true;
                                orig = candidate;
                            }
                            else
                            {
                                taps[j] = prev;
                            }
                        }
                    }
                }
            }
        }

        return (vTaps, hTaps, bestSse);
    }

    private static long EvaluateWienerSse(int[] plane, int[] source, int planeWidth, int planeHeight, int unitX, int unitY, int unitW, int unitH, int[] vTaps, int[] hTaps, bool isChroma, int[] vFilter, int[] hFilter, int[,] intermediate)
    {
        BuildWienerFilter(vTaps, isChroma, vFilter);
        BuildWienerFilter(hTaps, isChroma, hFilter);

        const int interRound0 = 3;
        const int interRound1 = 11;
        int offset = 1 << (BitDepth + FilterBits - interRound0 - 1);
        int limit = (1 << (BitDepth + 1 + FilterBits - interRound0)) - 1;

        int haloH = unitH + 6;
        for (int r = 0; r < haloH; r++)
        {
            int y = Math.Clamp(unitY + r - 3, 0, planeHeight - 1);
            int rowBase = y * planeWidth;
            for (int c = 0; c < unitW; c++)
            {
                long s = 0;
                for (int t = 0; t < 7; t++)
                {
                    int x = Math.Clamp(unitX + c + t - 3, 0, planeWidth - 1);
                    s += hFilter[t] * plane[rowBase + x];
                }

                int v = Round2(s, interRound0);
                intermediate[r, c] = Math.Clamp(v, -offset, limit - offset);
            }
        }

        long sse = 0;
        int maxSample = (1 << BitDepth) - 1;
        for (int r = 0; r < unitH; r++)
        {
            int srcRowBase = (unitY + r) * planeWidth;
            for (int c = 0; c < unitW; c++)
            {
                long s = 0;
                for (int t = 0; t < 7; t++)
                {
                    s += vFilter[t] * intermediate[r + t, c];
                }

                int v = Round2(s, interRound1);
                int outPixel = Math.Clamp(v, 0, maxSample);
                long diff = outPixel - source[srcRowBase + unitX + c];
                sse += diff * diff;
            }
        }

        return sse;
    }

    private static void BuildWienerFilter(int[] taps3, bool isChroma, int[] filter)
    {
        filter[3] = 128;
        for (int i = 0; i < 3; i++)
        {
            int c = isChroma && i == 0 ? 0 : taps3[i];
            filter[i] = c;
            filter[6 - i] = c;
            filter[3] -= 2 * c;
        }
    }

    private static int[][] BuildWienerRef()
    {
        var refs = new int[2][];
        refs[0] = (int[])WienerTapsMid.Clone();
        refs[1] = (int[])WienerTapsMid.Clone();
        return refs;
    }

    private static void UpdateWienerRef(int[][] refs, int[] vTaps, int[] hTaps)
    {
        vTaps.CopyTo(refs[0], 0);
        hTaps.CopyTo(refs[1], 0);
    }

    private static long CountWienerCoeffBits(int[] vTaps, int[] hTaps, int[][] refs, bool isChroma)
    {
        int firstIdx = isChroma ? 1 : 0;
        long bits = 0;
        foreach (var (taps, refTaps) in new[] { (vTaps, refs[0]), (hTaps, refs[1]) })
        {
            for (int j = firstIdx; j < 3; j++)
            {
                bits += CountSignedSubexpWithRefBits(WienerTapsMin[j], WienerTapsMax[j] + 1, WienerTapsK[j], refTaps[j], taps[j]);
            }
        }

        return bits;
    }

    // ---- SGR: real search over SgrSetCandidates, closed-form xqd solve + hill-climb, real SSE via the real box filter ----

    private static (int Set, int W0, int W1, long Sse) SearchSgrUnit(int[] reconPlane, int[] sourcePlane, int planeWidth, int planeHeight, int unitX, int unitY, int unitW, int unitH)
    {
        long bestSse = long.MaxValue;
        int bestSet = 0, bestW0 = 0, bestW1 = 0;

        foreach (int set in SgrSetCandidates)
        {
            int r0 = Av1SgrParams.Table[set][0];
            int r1 = Av1SgrParams.Table[set][2];

            int[,]? flt0 = r0 != 0 ? BoxFilterUnit(reconPlane, planeWidth, planeHeight, unitX, unitY, unitW, unitH, set, 0) : null;
            int[,]? flt1 = r1 != 0 ? BoxFilterUnit(reconPlane, planeWidth, planeHeight, unitX, unitY, unitW, unitH, set, 1) : null;

            (int w0, int w1) = SolveSgrXqd(reconPlane, sourcePlane, planeWidth, unitX, unitY, unitW, unitH, flt0, flt1, r0, r1);

            long sse = EvaluateSgrSse(reconPlane, sourcePlane, planeWidth, unitX, unitY, unitW, unitH, flt0, flt1, r0, r1, w0, w1);

            for (int step = 2; step >= 1; step--)
            {
                bool improved = true;
                while (improved)
                {
                    improved = false;
                    if (r0 != 0)
                    {
                        foreach (int dir in stackalloc[] { step, -step })
                        {
                            int candidate = Math.Clamp(w0 + dir, SgrprojXqdMin[0], SgrprojXqdMax[0]);
                            if (candidate == w0)
                            {
                                continue;
                            }

                            long s = EvaluateSgrSse(reconPlane, sourcePlane, planeWidth, unitX, unitY, unitW, unitH, flt0, flt1, r0, r1, candidate, w1);
                            if (s < sse)
                            {
                                sse = s;
                                w0 = candidate;
                                improved = true;
                            }
                        }
                    }

                    if (r1 != 0)
                    {
                        foreach (int dir in stackalloc[] { step, -step })
                        {
                            int candidate = Math.Clamp(w1 + dir, SgrprojXqdMin[1], SgrprojXqdMax[1]);
                            if (candidate == w1)
                            {
                                continue;
                            }

                            long s = EvaluateSgrSse(reconPlane, sourcePlane, planeWidth, unitX, unitY, unitW, unitH, flt0, flt1, r0, r1, w0, candidate);
                            if (s < sse)
                            {
                                sse = s;
                                w1 = candidate;
                                improved = true;
                            }
                        }
                    }
                }
            }

            if (sse < bestSse)
            {
                bestSse = sse;
                bestSet = set;
                bestW0 = w0;
                bestW1 = w1;
            }
        }

        return (bestSet, bestW0, bestW1, bestSse);
    }

    /// <summary>Closed-form initial xqd guess (master plan execution guide §4.2's real <c>get_proj_subspace</c>/<c>encode_xq</c> structure, using plain <see langword="double"/> arithmetic rather than libaom's own fixed-point-scaled Cramer's rule -- exact at these small (2x2 at most) problem sizes, no overflow risk either way, so this loses no real precision while skipping libaom's own overflow-avoidance machinery). Real per-unit hill-climbing (<see cref="SearchSgrUnit"/>) still validates/refines whatever this proposes against genuine SSE afterward.</summary>
    private static (int W0, int W1) SolveSgrXqd(int[] reconPlane, int[] sourcePlane, int planeWidth, int unitX, int unitY, int unitW, int unitH, int[,]? flt0, int[,]? flt1, int r0, int r1)
    {
        if (r0 == 0 && r1 == 0)
        {
            return (0, 0);
        }

        double h00 = 0, h01 = 0, h11 = 0, c0 = 0, c1 = 0;
        for (int i = 0; i < unitH; i++)
        {
            int rowBase = (unitY + i) * planeWidth;
            for (int j = 0; j < unitW; j++)
            {
                double u = (double)reconPlane[rowBase + unitX + j] * (1 << SgrprojRstBits);
                double s = ((double)sourcePlane[rowBase + unitX + j] * (1 << SgrprojRstBits)) - u;
                double f1 = r0 != 0 ? flt0![i, j] - u : 0;
                double f2 = r1 != 0 ? flt1![i, j] - u : 0;
                h00 += f1 * f1;
                h11 += f2 * f2;
                h01 += f1 * f2;
                c0 += f1 * s;
                c1 += f2 * s;
            }
        }

        double scale = 1 << SgrprojPrjBits;
        double x0 = 0, x1 = 0;
        if (r0 != 0 && r1 != 0)
        {
            double det = (h00 * h11) - (h01 * h01);
            if (Math.Abs(det) > 1e-9)
            {
                x0 = (((c0 * h11) - (c1 * h01)) / det) * scale;
                x1 = (((c1 * h00) - (c0 * h01)) / det) * scale;
            }
        }
        else if (r0 != 0)
        {
            if (h00 > 1e-9)
            {
                x0 = (c0 / h00) * scale;
            }
        }
        else if (r1 != 0)
        {
            if (h11 > 1e-9)
            {
                x1 = (c1 / h11) * scale;
            }
        }

        int w0 = r0 != 0 ? Math.Clamp((int)Math.Round(x0), SgrprojXqdMin[0], SgrprojXqdMax[0]) : 0;
        int w1 = r1 != 0 ? Math.Clamp((int)Math.Round(x1), SgrprojXqdMin[1], SgrprojXqdMax[1]) : 0;
        return (w0, w1);
    }

    private static long EvaluateSgrSse(int[] reconPlane, int[] sourcePlane, int planeWidth, int unitX, int unitY, int unitW, int unitH, int[,]? flt0, int[,]? flt1, int r0, int r1, int w0, int w1)
    {
        int w2 = (1 << SgrprojPrjBits) - w0 - w1;
        int maxSample = (1 << BitDepth) - 1;
        long sse = 0;

        for (int i = 0; i < unitH; i++)
        {
            int rowBase = (unitY + i) * planeWidth;
            for (int j = 0; j < unitW; j++)
            {
                long u = (long)reconPlane[rowBase + unitX + j] << SgrprojRstBits;
                long v = w1 * u;
                v += r0 != 0 ? (long)w0 * flt0![i, j] : w0 * u;
                v += r1 != 0 ? (long)w2 * flt1![i, j] : w2 * u;
                int outPixel = Math.Clamp(Round2(v, SgrprojRstBits + SgrprojPrjBits), 0, maxSample);
                long diff = outPixel - sourcePlane[rowBase + unitX + j];
                sse += diff * diff;
            }
        }

        return sse;
    }

    /// <summary>Locally edge-clamped port of <see cref="Av1LoopRestoration"/>'s own <c>BoxFilter</c> (spec §7.17.3) -- identical math, simple <see cref="Math.Clamp(int,int,int)"/> pixel access instead of the decoder's real stripe-aware <c>GetSourceSample</c> (see this type's own class remarks on why that's safe for search-time evaluation).</summary>
    private static int[,] BoxFilterUnit(int[] plane, int planeWidth, int planeHeight, int unitX, int unitY, int unitW, int unitH, int set, int pass)
    {
        int r = Av1SgrParams.Table[set][(pass * 2) + 0];
        int eps = Av1SgrParams.Table[set][(pass * 2) + 1];

        int n = ((2 * r) + 1) * ((2 * r) + 1);
        long n2e = (long)n * n * eps;
        long s = ((1L << SgrprojMtableBits) + (n2e / 2)) / n2e;

        var a = new int[unitH + 2, unitW + 2];
        var b = new int[unitH + 2, unitW + 2];

        int Sample(int px, int py) => plane[(Math.Clamp(py, 0, planeHeight - 1) * planeWidth) + Math.Clamp(px, 0, planeWidth - 1)];

        for (int i = -1; i < unitH + 1; i++)
        {
            for (int j = -1; j < unitW + 1; j++)
            {
                long aSum = 0;
                long bSum = 0;
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int c = Sample(unitX + j + dx, unitY + i + dy);
                        aSum += (long)c * c;
                        bSum += c;
                    }
                }

                long p = Math.Max(0, (aSum * n) - (bSum * bSum));
                long z = Round2(p * s, SgrprojMtableBits);

                int a2;
                if (z >= 255)
                {
                    a2 = 256;
                }
                else if (z == 0)
                {
                    a2 = 1;
                }
                else
                {
                    a2 = (int)(((z << SgrprojSgrBits) + (z / 2)) / (z + 1));
                }

                long oneOverN = ((1L << SgrprojRecipBits) + (n / 2)) / n;
                long b2 = ((1L << SgrprojSgrBits) - a2) * bSum * oneOverN;

                a[i + 1, j + 1] = a2;
                b[i + 1, j + 1] = Round2(b2, SgrprojRecipBits);
            }
        }

        var f = new int[unitH, unitW];
        for (int i = 0; i < unitH; i++)
        {
            int shift = 5;
            if (pass == 0 && (i & 1) != 0)
            {
                shift = 4;
            }

            for (int j = 0; j < unitW; j++)
            {
                long aAcc = 0;
                long bAcc = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int weight;
                        if (pass == 0)
                        {
                            weight = (i + dy) % 2 != 0 ? dx == 0 ? 6 : 5 : 0;
                        }
                        else
                        {
                            weight = dx == 0 || dy == 0 ? 4 : 3;
                        }

                        aAcc += weight * a[i + 1 + dy, j + 1 + dx];
                        bAcc += weight * b[i + 1 + dy, j + 1 + dx];
                    }
                }

                long v = (aAcc * Sample(unitX + j, unitY + i)) + bAcc;
                f[i, j] = Round2(v, SgrprojSgrBits + shift - SgrprojRstBits);
            }
        }

        return f;
    }

    private static void UpdateSgrRef(int[] refXqd, int set, int w0, int w1)
    {
        int r0 = Av1SgrParams.Table[set][0];
        int r1 = Av1SgrParams.Table[set][2];
        refXqd[0] = r0 != 0 ? w0 : refXqd[0];
        refXqd[1] = r1 != 0 ? w1 : refXqd[1];
    }

    private static long CountSgrCoeffBits(int set, int w0, int w1, int[] refXqd)
    {
        int r0 = Av1SgrParams.Table[set][0];
        int r1 = Av1SgrParams.Table[set][2];
        long bits = 0;
        if (r0 != 0)
        {
            bits += CountSignedSubexpWithRefBits(SgrprojXqdMin[0], SgrprojXqdMax[0] + 1, SgrprojPrjSubexpK, refXqd[0], w0);
        }

        if (r1 != 0)
        {
            bits += CountSignedSubexpWithRefBits(SgrprojXqdMin[1], SgrprojXqdMax[1] + 1, SgrprojPrjSubexpK, refXqd[1], w1);
        }

        return bits;
    }

    // ---- Subexp-with-reference bit counting (aom_count_primitive_refsubexpfin's own real algorithm -- master plan execution guide §5.3), mirroring Av1SymbolEncoder.WriteSignedSubexpWithRefBool's own real bit-length decisions exactly, just counting instead of emitting ----

    private static long CountSignedSubexpWithRefBits(int low, int high, int k, int r, int value) =>
        CountUnsignedSubexpWithRefBits(high - low, k, r - low, value - low);

    private static long CountUnsignedSubexpWithRefBits(int mx, int k, int r, int value)
    {
        int v = (r << 1) <= mx ? Recenter(r, value) : Recenter(mx - 1 - r, mx - 1 - value);
        return CountSubexpBits(mx, k, v);
    }

    private static long CountSubexpBits(int numSyms, int k, int v)
    {
        int i = 0;
        int mk = 0;
        long bits = 0;
        while (true)
        {
            int b2 = i != 0 ? k + i - 1 : k;
            int a = 1 << b2;
            if (numSyms <= mk + (3 * a))
            {
                bits += CountNsBits(v - mk, numSyms - mk);
                return bits;
            }

            if (v < mk + a)
            {
                return bits + 1 + b2;
            }

            bits++;
            i++;
            mk += a;
        }
    }

    private static long CountNsBits(int value, int n)
    {
        int w = Av1CdfAdaptation.FloorLog2((uint)n) + 1;
        int m = (1 << w) - n;
        return value < m ? w - 1 : w;
    }

    private static int Recenter(int r, int v)
    {
        if (v > (2 * r))
        {
            return v;
        }

        if (v >= r)
        {
            return (v - r) << 1;
        }

        return ((r - v) << 1) - 1;
    }

    private static int Round2(long x, int n) => n == 0 ? (int)x : (int)((x + (1L << (n - 1))) >> n);

    // ---- Final real application: reuse the real decoder filter (Av1LoopRestoration.Apply), stripe-aware, for the actual committed reconstruction ----

    /// <summary>
    /// Applies <paramref name="result"/>'s real, winning per-unit grids via the genuine, stripe-aware decoder
    /// filter (<see cref="Av1LoopRestoration.Apply"/>) -- unlike the search's own locally-clamped SSE
    /// evaluation, the actual committed reconstruction (and therefore what a real decoder reconstructs from
    /// the resulting bitstream) must be spec-exact, including the real pre-CDEF/post-CDEF stripe-boundary
    /// blend (spec §7.17.1) -- see this type's own class remarks.
    /// </summary>
    private static void ApplyFinal(Av1LoopRestorationSearchResult result, int[] reconY, int[]? reconU, int[]? reconV, int[] preCdefY, int[]? preCdefU, int[]? preCdefV, int width, int height, int chromaWidth, int chromaHeight, bool monoChrome)
    {
        int miCols = 2 * ((width + 7) >> 3);
        int miRows = 2 * ((height + 7) >> 3);
        int lumaLen = width * height;
        int chromaLen = chromaWidth * chromaHeight;

        var seq = BuildSequenceHeader(monoChrome);
        var frame = BuildFrameHeader(width, height, monoChrome, result);

        var (postY, postU, postV) = RentAndCopy(reconY, reconU, reconV, lumaLen, chromaLen);
        var (preY, preU, preV) = RentAndCopy(preCdefY, preCdefU, preCdefV, lumaLen, chromaLen);

        var decodeResult = new Av1FrameDecodeResult
        {
            Sequence = seq,
            Frame = frame,
            BlocksDecoded = 0,
            TilesStarted = 0,
            Planes = [postY, postU ?? [], postV ?? []],
            PlaneWidths = postU is null ? [width, 0, 0] : [width, chromaWidth, chromaWidth],
            PlaneHeights = postU is null ? [height, 0, 0] : [height, chromaHeight, chromaHeight],
            StoppedAtResidual = false,
            YModes = [],
            UvModes = [],
            MiSizes = [],
            PaletteSizesY = [],
            PaletteSizesUV = [],
            PaletteColorsYGrid = [],
            PaletteColorsUGrid = [],
            IsInters = [],
            MvRowsGrid = [],
            MvColsGrid = [],
            AngleDeltaYGrid = [],
            AngleDeltaUvGrid = [],
            UseFilterIntraGrid = [],
            FilterIntraModeGrid = [],
            Skips = new bool[miCols * miRows],
            SegmentIds = new int[miCols * miRows],
            DeltaLfs = [new int[miCols * miRows], new int[miCols * miRows], new int[miCols * miRows], new int[miCols * miRows]],
            LoopfilterTxSizes = [[], [], []],
            LoopfilterTxSizeStrides = [0, 0, 0],
            CdefIdx = new int[miCols * miRows],
            RestorationUnits = result.Grids,
            MinSymbolMaxBitsAtExit = 0,
        };

        int[][] deblockedPlanes = monoChrome ? [preY, [], []] : [preY, preU!, preV!];
        Av1LoopRestoration.Apply(decodeResult, deblockedPlanes);

        // Av1LoopRestoration.Apply already returned deblockedPlanes and the ORIGINAL result.Planes (postY/U/V
        // above) to the pool internally, replacing result.Planes with a fresh set of pool-rented "LrPlanes" --
        // those are what's copied out below and returned here, mirroring Av1CdefSearch's own identical
        // buffer-ownership discipline.
        Array.Copy(decodeResult.Planes[0], reconY, lumaLen);
        ArrayPool<int>.Shared.Return(decodeResult.Planes[0]);
        if (!monoChrome)
        {
            Array.Copy(decodeResult.Planes[1], reconU!, chromaLen);
            Array.Copy(decodeResult.Planes[2], reconV!, chromaLen);
            ArrayPool<int>.Shared.Return(decodeResult.Planes[1]);
            ArrayPool<int>.Shared.Return(decodeResult.Planes[2]);
        }
    }

    private static (int[] Y, int[]? U, int[]? V) RentAndCopy(int[] y, int[]? u, int[]? v, int lumaLen, int chromaLen)
    {
        int[] rentedY = ArrayPool<int>.Shared.Rent(lumaLen);
        Array.Copy(y, rentedY, lumaLen);

        if (u is null)
        {
            return (rentedY, null, null);
        }

        int[] rentedU = ArrayPool<int>.Shared.Rent(chromaLen);
        Array.Copy(u, rentedU, chromaLen);
        int[] rentedV = ArrayPool<int>.Shared.Rent(chromaLen);
        Array.Copy(v!, rentedV, chromaLen);
        return (rentedY, rentedU, rentedV);
    }

    private static Av1SequenceHeader BuildSequenceHeader(bool monoChrome) => new()
    {
        SeqProfile = Av1SequenceHeaderWriter.SeqProfile,
        SeqLevelIdx0 = Av1SequenceHeaderWriter.SeqLevelIdx0,
        FrameWidthBits = 0,
        FrameHeightBits = 0,
        MaxFrameWidth = 0,
        MaxFrameHeight = 0,
        Use128x128Superblock = false,
        EnableFilterIntra = true,
        EnableIntraEdgeFilter = Av1SequenceHeaderWriter.EnableIntraEdgeFilter,
        EnableSuperres = false,
        EnableCdef = false,
        EnableRestoration = true,
        BitDepth = BitDepth,
        MonoChrome = monoChrome,
        ColorPrimaries = Av1SequenceHeaderWriter.ColorPrimaries,
        TransferCharacteristics = Av1SequenceHeaderWriter.TransferCharacteristics,
        MatrixCoefficients = Av1SequenceHeaderWriter.MatrixCoefficients,
        ColorRange = Av1SequenceHeaderWriter.ColorRangeFull,
        SubsamplingX = true,
        SubsamplingY = true,
        ChromaSamplePosition = Av1SequenceHeaderWriter.ChromaSamplePosition,
        SeparateUvDeltaQ = false,
        FilmGrainParamsPresent = false,
    };

    private static Av1FrameHeader BuildFrameHeader(int width, int height, bool monoChrome, Av1LoopRestorationSearchResult result)
    {
        int miCols = 2 * ((width + 7) >> 3);
        int miRows = 2 * ((height + 7) >> 3);

        return new Av1FrameHeader
        {
            FrameWidth = width,
            FrameHeight = height,
            UpscaledWidth = width,
            RenderWidth = width,
            RenderHeight = height,
            MiCols = miCols,
            MiRows = miRows,
            AllowScreenContentTools = false,
            AllowIntrabc = false,
            BaseQIdx = 1,
            DeltaQYDc = 0,
            DeltaQUDc = 0,
            DeltaQUAc = 0,
            DeltaQVDc = 0,
            DeltaQVAc = 0,
            UsingQMatrix = false,
            QmY = 0,
            QmU = 0,
            QmV = 0,
            Segmentation = new Av1SegmentationParams
            {
                Enabled = false,
                FeatureEnabled = new bool[Av1SegmentationParams.MaxSegments, Av1SegmentationParams.SegLvlMax],
                FeatureData = new int[Av1SegmentationParams.MaxSegments, Av1SegmentationParams.SegLvlMax],
                SegIdPreSkip = false,
                LastActiveSegId = 0,
            },
            DeltaQPresent = false,
            DeltaQRes = 0,
            DeltaLfPresent = false,
            DeltaLfRes = 0,
            DeltaLfMulti = false,
            CodedLossless = false,
            AllLossless = false,
            LoopFilter = new Av1LoopFilterParams
            {
                Level = [0, 0, 0, 0],
                Sharpness = 0,
                DeltaEnabled = false,
                RefDeltas = [1, 0, 0, 0, -1, 0, -1, -1],
                ModeDeltas = [0, 0],
            },
            Cdef = new Av1CdefParams
            {
                Damping = 3,
                Bits = 0,
                YPriStrength = [0],
                YSecStrength = [0],
                UvPriStrength = [0],
                UvSecStrength = [0],
            },
            LoopRestoration = new Av1LoopRestorationParams
            {
                FrameRestorationType = result.FrameRestorationType,
                UsesLr = result.UsesLr,
                UnitSize = result.UnitSize,
            },
            TxMode = Av1FrameHeader.TxModeSelect,
            ReducedTxSet = true,
            TileInfo = null!,
            DisableCdfUpdate = false,
        };
    }
}

/// <summary>
/// One frame's real loop-restoration search result: the frame-wide <c>lr_params()</c> fields
/// (<see cref="FrameRestorationType"/>/<see cref="UsesLr"/>/<see cref="UnitSize"/>, spec §5.9.20) plus the
/// real per-unit grid each present plane needs (<see cref="Grids"/>, reusing the decoder's own
/// <see cref="Av1RestorationUnitGrid"/> -- the same "real per-unit assignment, real decoder types" pattern
/// <see cref="Av1CdefSearchResult"/> already established for CDEF).
/// </summary>
internal sealed class Av1LoopRestorationSearchResult
{
    /// <summary>Length 3 -- one of <see cref="Av1LoopRestorationParams"/>'s <c>Restore*</c> constants per plane.</summary>
    public required int[] FrameRestorationType { get; init; }

    /// <summary><see langword="true"/> whenever any plane's own <see cref="FrameRestorationType"/> isn't <c>RestoreNone</c> -- spec's own <c>UsesLr</c>, gates whether <c>lr_params()</c> writes a unit-size shift at all.</summary>
    public required bool UsesLr { get; init; }

    /// <summary>Length 3, one restoration-unit pixel size per plane (0 for a plane using <c>RestoreNone</c>).</summary>
    public required int[] UnitSize { get; init; }

    /// <summary>Length 3 -- <see langword="null"/> for a monochrome frame's chroma planes, or any plane whose own <see cref="FrameRestorationType"/> is <c>RestoreNone</c> (nothing to signal per-unit there).</summary>
    public required Av1RestorationUnitGrid?[] Grids { get; init; }

    public static readonly Av1LoopRestorationSearchResult Off = new()
    {
        FrameRestorationType = [Av1LoopRestorationParams.RestoreNone, Av1LoopRestorationParams.RestoreNone, Av1LoopRestorationParams.RestoreNone],
        UsesLr = false,
        UnitSize = [0, 0, 0],
        Grids = [null, null, null],
    };
}
