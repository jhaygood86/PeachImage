namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Independent transcription of libaom's own real <c>av1_calc_indices_dim1_c</c>/<c>av1_calc_indices_dim2_c</c>
/// (<c>av1/encoder/k_means_template.h</c>'s own <c>RENAME_C(av1_calc_indices)</c>/<c>calc_dist</c>, expanded
/// for both <c>AV1_K_MEANS_DIM</c> instantiations), kept deliberately separate from
/// <see cref="PeachImage.Formats.Avif.Encoder.Av1.Av1PaletteSearch.CalcIndices1D"/>/<c>CalcIndices2D</c>'s
/// own implementation so a test comparing the two is a genuine check against libaom's own real algorithm.
/// </summary>
internal static class LibaomReferenceKMeans
{
    /// <summary>Dim-1 (L1-distance, squared-on-accumulation) nearest-centroid assignment.</summary>
    public static long CalcIndicesDim1(ReadOnlySpan<short> data, ReadOnlySpan<short> centroids, Span<byte> indices, int n, int k)
    {
        long dist = 0;
        for (int i = 0; i < n; i++)
        {
            int minDist = Math.Abs(data[i] - centroids[0]);
            byte best = 0;
            for (int j = 1; j < k; j++)
            {
                int thisDist = Math.Abs(data[i] - centroids[j]);
                if (thisDist < minDist)
                {
                    minDist = thisDist;
                    best = (byte)j;
                }
            }

            indices[i] = best;
            dist += (long)minDist * minDist;
        }

        return dist;
    }

    /// <summary>Dim-2 (squared-Euclidean) nearest-centroid assignment -- <paramref name="data"/>/<paramref name="centroids"/> interleaved as (u0, v0, u1, v1, ...), matching libaom's own real <c>AV1_K_MEANS_DIM == 2</c> memory layout.</summary>
    public static long CalcIndicesDim2(ReadOnlySpan<short> data, ReadOnlySpan<short> centroids, Span<byte> indices, int n, int k)
    {
        long dist = 0;
        for (int i = 0; i < n; i++)
        {
            int du = data[(i * 2) + 0] - centroids[0];
            int dv = data[(i * 2) + 1] - centroids[1];
            int minDist = (du * du) + (dv * dv);
            byte best = 0;
            for (int j = 1; j < k; j++)
            {
                du = data[(i * 2) + 0] - centroids[(j * 2) + 0];
                dv = data[(i * 2) + 1] - centroids[(j * 2) + 1];
                int thisDist = (du * du) + (dv * dv);
                if (thisDist < minDist)
                {
                    minDist = thisDist;
                    best = (byte)j;
                }
            }

            indices[i] = best;
            dist += minDist;
        }

        return dist;
    }
}
