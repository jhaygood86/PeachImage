namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Literal port of libaom's real palette-mode candidate-generation primitives (<c>av1/encoder/palette.c</c>,
/// <c>av1/encoder/k_means_template.h</c>, <c>av1/encoder/random.h</c>, read directly from the local checkout
/// at <c>C:\Sources\GoogleSource\aom</c>) -- the actual algorithms real AV1 encoders use to propose palette
/// color sets, not an independently-designed equivalent. See <c>THIRD-PARTY-LICENSES.md</c> for the full
/// attribution.
///
/// <para>Pure, stateless math: no <c>TileState</c>/CDF/cost dependency at all. <c>Av1TileEncoder.cs</c>'s own
/// <c>SearchLosslessYPalette</c>/<c>SearchLosslessUvPalette</c> own the real RD-search orchestration
/// (candidate ordering per <see cref="Av1SpeedFeatures.PrunePaletteSearchLevel"/>, header-cost gating per
/// <see cref="Av1SpeedFeatures.PruneLumaPaletteSizeSearchLevel"/>, real bit-cost evaluation via this
/// project's own existing <c>EstimateColorMapBits</c>/<c>ComputePaletteResidualCost</c>) that calls into
/// these primitives -- mirroring <c>av1_rd_pick_palette_intra_sby</c>/<c>av1_rd_pick_palette_intra_sbuv</c>'s
/// own split between the top-level RD search and this file's lower-level <c>find_top_colors</c>/
/// <c>av1_k_means</c>/<c>av1_calc_indices</c> primitives.</para>
///
/// <para><see cref="OptimizePaletteColors"/> ports libaom's own <c>optimize_palette_colors</c>
/// (<c>palette.c</c>) -- "bias toward using colors in the cache": each candidate centroid is snapped to a
/// nearby (within <c>4 &lt;&lt; (bit_depth-8)</c> codeword units) already-cached neighbor color when one
/// exists, trading a tiny reconstruction error for reusing an already-cheap cached color value instead of
/// paying to re-signal a near-duplicate one. <see cref="IndexColorCache"/> ports the matching
/// <c>av1_index_color_cache</c> (also <c>palette.c</c>), which both the real per-candidate cost estimate and
/// the real bitstream writer in <c>Av1TileEncoder.cs</c> use to determine which of a chosen palette's colors
/// actually get pulled from the cache (and so which cache-slot bypass bits get written) versus explicitly
/// transmitted.</para>
/// </summary>
internal static class Av1PaletteSearch
{
    /// <summary><c>PALETTE_MAX_SIZE</c> (<c>av1/common/enums.h</c>).</summary>
    internal const int PaletteMaxSize = 8;

    /// <summary><c>PALETTE_MIN_SIZE</c> (<c>av1/common/enums.h</c>).</summary>
    internal const int PaletteMinSize = 2;

    /// <summary><c>MAX_PALETTE_BLOCK_WIDTH</c> * <c>MAX_PALETTE_BLOCK_HEIGHT</c> (<c>av1/common/blockd.h</c>) -- the largest pixel count any palette-eligible block can ever have, sizing every fixed scratch buffer below.</summary>
    internal const int MaxPaletteBlockPixels = 64 * 64;

    /// <summary>
    /// <c>av1_count_colors</c> (<c>av1/encoder/intra_mode_search.c:320-336</c>): a full 256-bin histogram
    /// over one plane's pixels in <c>[x, x+w) x [y, y+h)</c>, returning the number of distinct 8-bit values
    /// actually present. <paramref name="countBuf"/> must be at least 256 long; cleared internally.
    /// </summary>
    internal static int CountColors(int[] source, int stride, int x, int y, int w, int h, int[] countBuf)
    {
        Array.Clear(countBuf, 0, 256);
        for (int i = 0; i < h; i++)
        {
            int rowBase = ((y + i) * stride) + x;
            for (int j = 0; j < w; j++)
            {
                countBuf[source[rowBase + j]]++;
            }
        }

        int n = 0;
        for (int i = 0; i < 256; i++)
        {
            if (countBuf[i] != 0)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// <c>find_top_colors</c> (<c>av1/encoder/palette.c:502-538</c>): the <paramref name="nColors"/> most
    /// frequent values in <paramref name="countBuf"/>, kept as a bounded, incrementally-maintained top-N list
    /// (not a full sort of every distinct color) -- ties broken toward the lower color value
    /// (<c>color_count_comp</c>'s own tie-break). <paramref name="topColors"/> must be at least
    /// <paramref name="nColors"/> long.
    /// </summary>
    internal static void FindTopColors(int[] countBuf, int nColors, Span<int> topColors)
    {
        Span<int> idx = stackalloc int[PaletteMaxSize];
        Span<int> cnt = stackalloc int[PaletteMaxSize];
        int n = 0;

        for (int i = 0; i < 256; i++)
        {
            int count = countBuf[i];
            if (count <= 0)
            {
                continue;
            }

            if (n < nColors)
            {
                idx[n] = i;
                cnt[n] = count;
                n++;
                if (n == nColors)
                {
                    // qsort(top_color_counts, n_colors, ..., color_count_comp): descending count, ascending
                    // index on ties. n <= PaletteMaxSize (8), and every index is globally unique, so a plain
                    // insertion sort produces the exact same (unique) total order qsort would -- there is
                    // only one way to sort a set of distinct keys.
                    for (int a = 1; a < n; a++)
                    {
                        int keyIdx = idx[a];
                        int keyCnt = cnt[a];
                        int b = a - 1;
                        while (b >= 0 && (cnt[b] < keyCnt || (cnt[b] == keyCnt && idx[b] > keyIdx)))
                        {
                            idx[b + 1] = idx[b];
                            cnt[b + 1] = cnt[b];
                            b--;
                        }

                        idx[b + 1] = keyIdx;
                        cnt[b + 1] = keyCnt;
                    }
                }
            }
            else if (count > cnt[nColors - 1])
            {
                // "Check the worst in the sorted top... Move up to the best one." -- shift-down insertion,
                // count-only comparison (matches palette.c's own `count_buf[i] > top_color_counts[j-1].count`
                // exactly; no index tie-break during this incremental phase, only in the initial full sort
                // above).
                int j = nColors - 1;
                while (j >= 1 && count > cnt[j - 1])
                {
                    j--;
                }

                for (int k = nColors - 1; k > j; k--)
                {
                    idx[k] = idx[k - 1];
                    cnt[k] = cnt[k - 1];
                }

                idx[j] = i;
                cnt[j] = count;
            }
        }

        for (int i = 0; i < nColors; i++)
        {
            topColors[i] = idx[i];
        }
    }

    /// <summary>
    /// <c>remove_duplicates</c> (<c>palette.c:49-61</c>): sorts <paramref name="centroids"/> ascending and
    /// collapses consecutive duplicates in place, returning the resulting unique count.
    /// </summary>
    internal static int RemoveDuplicates(Span<int> centroids, int count)
    {
        centroids[..count].Sort();
        int n = 1;
        for (int i = 1; i < count; i++)
        {
            if (centroids[i] != centroids[i - 1])
            {
                centroids[n++] = centroids[i];
            }
        }

        return n;
    }

    /// <summary>
    /// <c>optimize_palette_colors</c> (<c>av1/encoder/palette.c</c>): "Bias toward using colors in the
    /// cache." For each of <paramref name="centroids"/> (called on the raw, pre-<see cref="RemoveDuplicates"/>
    /// candidate set, exactly matching libaom's own call order in <c>palette_rd_y</c>/
    /// <c>av1_rd_pick_palette_intra_sbuv</c>), finds the nearest value in <paramref name="cache"/> and snaps
    /// the centroid to it whenever that distance is <c>&lt;= 4</c> (this project's own fixed 8-bit depth
    /// makes libaom's own <c>4 &lt;&lt; (bit_depth - 8)</c> threshold always exactly 4). A pure, unconditional
    /// per-centroid snap -- no RD comparison here, matching libaom's own real code exactly: every candidate
    /// gets this treatment before it's ever costed, not just candidates that turn out to win.
    /// </summary>
    internal static void OptimizePaletteColors(ReadOnlySpan<int> cache, int nCache, Span<int> centroids, int n)
    {
        if (nCache <= 0)
        {
            return;
        }

        const int minThreshold = 4;
        for (int i = 0; i < n; i++)
        {
            int minDiff = Math.Abs(centroids[i] - cache[0]);
            int idx = 0;
            for (int j = 1; j < nCache; j++)
            {
                int diff = Math.Abs(centroids[i] - cache[j]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    idx = j;
                }
            }

            if (minDiff <= minThreshold)
            {
                centroids[i] = cache[idx];
            }
        }
    }

    /// <summary>
    /// <c>av1_index_color_cache</c> (<c>av1/encoder/palette.c</c>): determines which of <paramref name="colors"/>
    /// (an already-ascending, already-deduplicated palette) are actually pulled from
    /// <paramref name="cache"/> versus need explicit transmission -- shared by the real per-candidate cost
    /// estimate and the real bitstream writer in <c>Av1TileEncoder.cs</c>, so the bits estimated always match
    /// the bits actually written.
    ///
    /// <para>Walks <paramref name="cache"/> in order, stopping the instant every one of
    /// <paramref name="colors"/> has been matched -- mirroring the real bitstream's own early-stop protocol
    /// exactly (<c>Av1TileDecoder.ReadPaletteColorsY</c>/<c>Uv</c>'s identical <c>i &lt; nCache &amp;&amp; idx
    /// &lt; n</c> loop condition): cache slots past that point never get a bypass bit read or written at all.
    /// <paramref name="cacheColorFound"/> (length &gt;= <paramref name="nCache"/>) receives which of the
    /// first <paramref name="slotsChecked"/> slots matched (the bypass-bit values to write, in cache order);
    /// <paramref name="outExplicitColors"/> (length &gt;= <paramref name="n"/>) receives the not-matched
    /// colors, in their original (already-ascending) relative order -- these are the ones that still need
    /// literal-plus-delta transmission, coded as their own independent ascending run, exactly as
    /// <c>Av1TileDecoder.ReadPaletteColorsY</c>/<c>Uv</c> reconstruct them before the final cache-merge.
    /// Returns the number of explicit colors (<c><paramref name="n"/> - matched count</c>).
    /// </para>
    /// </summary>
    internal static int IndexColorCache(ReadOnlySpan<int> cache, int nCache, ReadOnlySpan<int> colors, int n, Span<bool> cacheColorFound, Span<int> outExplicitColors, out int slotsChecked)
    {
        if (nCache <= 0)
        {
            colors[..n].CopyTo(outExplicitColors);
            slotsChecked = 0;
            return n;
        }

        Span<bool> inCacheFlags = stackalloc bool[PaletteMaxSize];
        inCacheFlags.Clear();
        int nInCache = 0;
        int i = 0;
        for (; i < nCache && nInCache < n; i++)
        {
            bool found = false;
            for (int j = 0; j < n; j++)
            {
                if (colors[j] == cache[i])
                {
                    inCacheFlags[j] = true;
                    found = true;
                    nInCache++;
                    break;
                }
            }

            cacheColorFound[i] = found;
        }

        slotsChecked = i;

        int k = 0;
        for (int idx = 0; idx < n; idx++)
        {
            if (!inCacheFlags[idx])
            {
                outExplicitColors[k++] = colors[idx];
            }
        }

        return k;
    }

    /// <summary><c>av1/encoder/random.h</c>'s <c>lcg_next</c> -- exact port (the empty-cluster reseed below needs bit-identical output to libaom's own, since it's a real, if rare, data-dependent branch in cluster assignment).</summary>
    private static uint LcgNext(ref uint state)
    {
        state = (uint)((state * 1103515245u) + 12345u);
        return state;
    }

    /// <summary><c>lcg_rand16</c> (<c>av1/encoder/random.h</c>): a value in <c>[0, 32768)</c>.</summary>
    private static uint LcgRand16(ref uint state) => (LcgNext(ref state) / 65536u) % 32768u;

    private static long DivideAndRound(long x, long y) => (x + (y >> 1)) / y;

    /// <summary>
    /// <c>av1_k_means</c> (dim=1) (<c>av1/encoder/k_means_template.h</c>): Lloyd's algorithm with libaom's
    /// own specific, deterministic-but-data-dependent quirks -- the monotonic-non-increase early-termination
    /// rule (roll back to the previous iteration's centroids/assignment the instant total distortion would
    /// increase) and the pseudo-random (LCG, freshly seeded from <c>data[0]</c> on every centroid-update call)
    /// empty-cluster reseed. <paramref name="centroids"/> is both the initial seed (input) and the final
    /// result (output, length <paramref name="k"/>); <paramref name="indices"/> receives the final per-pixel
    /// assignment (length <paramref name="n"/>, at most <see cref="MaxPaletteBlockPixels"/>).
    ///
    /// <para>Uses named "current"/"previous" locals instead of libaom's own index-toggling
    /// <c>meta_centroids[2]</c>/<c>meta_indices[2]</c> ping-pong buffers -- purely a memory-layout
    /// difference (C reuses the caller's own output arrays as one of the two buffers to avoid an allocation;
    /// this always uses fixed scratch). The sequence of values computed, compared, and ultimately kept is
    /// identical either way.</para>
    /// </summary>
    internal static void KMeans1D(ReadOnlySpan<int> data, Span<int> centroids, Span<int> indices, int n, int k, int maxItr)
    {
        Span<int> prevCentroids = stackalloc int[PaletteMaxSize];
        Span<int> curCentroids = stackalloc int[PaletteMaxSize];
        Span<int> prevIndices = stackalloc int[MaxPaletteBlockPixels];
        Span<int> curIndices = stackalloc int[MaxPaletteBlockPixels];

        centroids[..k].CopyTo(prevCentroids);
        long prevDist = CalcIndices1D(data, prevCentroids[..k], prevIndices[..n], n, k);

        for (int iter = 0; iter < maxItr; iter++)
        {
            CalcCentroids1D(data, prevIndices[..n], curCentroids[..k], n, k);
            if (curCentroids[..k].SequenceEqual(prevCentroids[..k]))
            {
                break;
            }

            long thisDist = CalcIndices1D(data, curCentroids[..k], curIndices[..n], n, k);
            if (thisDist > prevDist)
            {
                break;
            }

            curCentroids[..k].CopyTo(prevCentroids);
            curIndices[..n].CopyTo(prevIndices);
            prevDist = thisDist;
        }

        prevCentroids[..k].CopyTo(centroids);
        prevIndices[..n].CopyTo(indices);
    }

    /// <summary>(U, V)-pair counterpart to <see cref="KMeans1D"/> -- see its own remarks; uses squared-Euclidean distance in (U, V) space throughout (assignment and total distortion alike), matching <c>av1_k_means</c>'s dim=2 instantiation exactly.</summary>
    internal static void KMeans2D(ReadOnlySpan<int> dataU, ReadOnlySpan<int> dataV, Span<int> centroidU, Span<int> centroidV, Span<int> indices, int n, int k, int maxItr)
    {
        Span<int> prevCentroidU = stackalloc int[PaletteMaxSize];
        Span<int> prevCentroidV = stackalloc int[PaletteMaxSize];
        Span<int> curCentroidU = stackalloc int[PaletteMaxSize];
        Span<int> curCentroidV = stackalloc int[PaletteMaxSize];
        Span<int> prevIndices = stackalloc int[MaxPaletteBlockPixels];
        Span<int> curIndices = stackalloc int[MaxPaletteBlockPixels];

        centroidU[..k].CopyTo(prevCentroidU);
        centroidV[..k].CopyTo(prevCentroidV);
        long prevDist = CalcIndices2D(dataU, dataV, prevCentroidU[..k], prevCentroidV[..k], prevIndices[..n], n, k);

        for (int iter = 0; iter < maxItr; iter++)
        {
            CalcCentroids2D(dataU, dataV, prevIndices[..n], curCentroidU[..k], curCentroidV[..k], n, k);
            if (curCentroidU[..k].SequenceEqual(prevCentroidU[..k]) && curCentroidV[..k].SequenceEqual(prevCentroidV[..k]))
            {
                break;
            }

            long thisDist = CalcIndices2D(dataU, dataV, curCentroidU[..k], curCentroidV[..k], curIndices[..n], n, k);
            if (thisDist > prevDist)
            {
                break;
            }

            curCentroidU[..k].CopyTo(prevCentroidU);
            curCentroidV[..k].CopyTo(prevCentroidV);
            curIndices[..n].CopyTo(prevIndices);
            prevDist = thisDist;
        }

        prevCentroidU[..k].CopyTo(centroidU);
        prevCentroidV[..k].CopyTo(centroidV);
        prevIndices[..n].CopyTo(indices);
    }

    /// <summary><c>av1_calc_indices</c> (dim=1) (<c>k_means_template.h:47-71</c>): nearest-centroid (L1) assignment for every pixel, returning total distortion (libaom accumulates the L1 distance <em>squared</em> for dim 1, <c>*dist += min_dist * min_dist</c>). Ties go to the lowest centroid index (strict <c>&lt;</c> comparison, matching libaom exactly).</summary>
    internal static long CalcIndices1D(ReadOnlySpan<int> data, ReadOnlySpan<int> centroids, Span<int> indices, int n, int k)
    {
        long dist = 0;
        for (int i = 0; i < n; i++)
        {
            int minDist = Math.Abs(data[i] - centroids[0]);
            int best = 0;
            for (int j = 1; j < k; j++)
            {
                int d = Math.Abs(data[i] - centroids[j]);
                if (d < minDist)
                {
                    minDist = d;
                    best = j;
                }
            }

            indices[i] = best;
            dist += (long)minDist * minDist;
        }

        return dist;
    }

    /// <summary>(U, V)-pair counterpart to <see cref="CalcIndices1D"/> -- squared-Euclidean distance, no extra squaring on accumulation (already squared).</summary>
    internal static long CalcIndices2D(ReadOnlySpan<int> dataU, ReadOnlySpan<int> dataV, ReadOnlySpan<int> centroidU, ReadOnlySpan<int> centroidV, Span<int> indices, int n, int k)
    {
        long dist = 0;
        for (int i = 0; i < n; i++)
        {
            int du = dataU[i] - centroidU[0];
            int dv = dataV[i] - centroidV[0];
            int minDist = (du * du) + (dv * dv);
            int best = 0;
            for (int j = 1; j < k; j++)
            {
                du = dataU[i] - centroidU[j];
                dv = dataV[i] - centroidV[j];
                int d = (du * du) + (dv * dv);
                if (d < minDist)
                {
                    minDist = d;
                    best = j;
                }
            }

            indices[i] = best;
            dist += minDist;
        }

        return dist;
    }

    /// <summary><c>calc_centroids</c> (dim=1) (<c>k_means_template.h:73-104</c>): recomputes each cluster's centroid as the round-to-nearest mean of its assigned pixels, or -- for a cluster with zero members -- a pseudo-random existing pixel value, chosen via an LCG freshly seeded from <c>data[0]</c> <em>on every call</em> (not persisted across k-means iterations), advanced sequentially across however many clusters empty out in this same call.</summary>
    private static void CalcCentroids1D(ReadOnlySpan<int> data, ReadOnlySpan<int> indices, Span<int> centroids, int n, int k)
    {
        Span<int> count = stackalloc int[PaletteMaxSize];
        Span<long> sum = stackalloc long[PaletteMaxSize];
        uint randState = (uint)data[0];

        for (int i = 0; i < n; i++)
        {
            int idx = indices[i];
            count[idx]++;
            sum[idx] += data[i];
        }

        for (int i = 0; i < k; i++)
        {
            if (count[i] == 0)
            {
                centroids[i] = data[(int)(LcgRand16(ref randState) % (uint)n)];
            }
            else
            {
                centroids[i] = (int)DivideAndRound(sum[i], count[i]);
            }
        }
    }

    /// <summary>(U, V)-pair counterpart to <see cref="CalcCentroids1D"/> -- the LCG reseed is keyed off <c>dataU[0]</c> (libaom's own interleaved <c>data[0]</c> is the first pixel's U component, since its layout is <c>(u0, v0, u1, v1, ...)</c>), and an empty cluster's reseed copies <em>both</em> components from the same randomly-chosen pixel index.</summary>
    private static void CalcCentroids2D(ReadOnlySpan<int> dataU, ReadOnlySpan<int> dataV, ReadOnlySpan<int> indices, Span<int> centroidU, Span<int> centroidV, int n, int k)
    {
        Span<int> count = stackalloc int[PaletteMaxSize];
        Span<long> sumU = stackalloc long[PaletteMaxSize];
        Span<long> sumV = stackalloc long[PaletteMaxSize];
        uint randState = (uint)dataU[0];

        for (int i = 0; i < n; i++)
        {
            int idx = indices[i];
            count[idx]++;
            sumU[idx] += dataU[i];
            sumV[idx] += dataV[i];
        }

        for (int i = 0; i < k; i++)
        {
            if (count[i] == 0)
            {
                int pick = (int)(LcgRand16(ref randState) % (uint)n);
                centroidU[i] = dataU[pick];
                centroidV[i] = dataV[pick];
            }
            else
            {
                centroidU[i] = (int)DivideAndRound(sumU[i], count[i]);
                centroidV[i] = (int)DivideAndRound(sumV[i], count[i]);
            }
        }
    }
}
