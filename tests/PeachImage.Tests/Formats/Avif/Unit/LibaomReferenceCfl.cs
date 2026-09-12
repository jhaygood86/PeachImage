namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Independent transcription of libaom's own real, three-stage CFL (chroma-from-luma) pipeline
/// (<c>av1/common/cfl.c</c>'s own <c>cfl_luma_subsampling_420/422/444_lbd_c</c>, <c>subtract_average_c</c>,
/// <c>cfl_predict_lbd_c</c>/<c>get_scaled_luma_q0</c>), kept deliberately separate from
/// <see cref="PeachImage.Formats.Avif.Decoding.Av1.Av1IntraPrediction.PredictChromaFromLuma"/>'s own single
/// fused pass (built directly from the AV1 spec's own §7.11.5 text, which unifies subsample/subtract-
/// average/predict into one loop rather than libaom's own three separate buffer-passing stages) so a test
/// comparing the two is a genuine two-independent-sources check, not a self-consistency check against one
/// transcription. This project's own encoder is 8-bit only, so only the low-bit-depth (<c>_lbd</c>) variants
/// are transcribed here.
/// </summary>
internal static class LibaomReferenceCfl
{
    /// <summary>
    /// <c>cfl_luma_subsampling_420/422/444_lbd_c</c>, unified into one function selected by
    /// <paramref name="subX"/>/<paramref name="subY"/> (matching
    /// <see cref="PeachImage.Formats.Avif.Decoding.Av1.Av1IntraPrediction.PredictChromaFromLuma"/>'s own
    /// single parameterized subsample loop rather than libaom's three separate named functions) -- produces
    /// <c>recon_buf_q3</c>: each output sample is the sum of the <c>(1 &lt;&lt; subX) * (1 &lt;&lt; subY)</c>
    /// luma samples it covers, left-shifted by <c>3 - subX - subY</c> so every chroma format's output ends
    /// up on the same Q3 (1/8-pixel) scale regardless of how many luma samples were summed.
    /// </summary>
    public static void Subsample(int[] luma, int lumaStride, int lumaX0, int lumaY0, int[] outputQ3, int w, int h, int subX, int subY)
    {
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int lx = lumaX0 + (i << subX);
                int ly = lumaY0 + (j << subY);
                int sum = 0;
                for (int dy = 0; dy <= subY; dy++)
                {
                    for (int dx = 0; dx <= subX; dx++)
                    {
                        sum += luma[((ly + dy) * lumaStride) + lx + dx];
                    }
                }

                outputQ3[(j * w) + i] = sum << (3 - subX - subY);
            }
        }
    }

    /// <summary><c>subtract_average_c</c>: <c>avg = round_power_of_two(sum, num_pel_log2)</c>, then <c>dst[i] = src[i] - avg</c>.</summary>
    public static void SubtractAverage(int[] reconQ3, int[] acQ3, int w, int h)
    {
        int numPelLog2 = 0;
        while ((1 << numPelLog2) < w * h)
        {
            numPelLog2++;
        }

        int sum = 1 << (numPelLog2 - 1); // round_offset.
        for (int i = 0; i < w * h; i++)
        {
            sum += reconQ3[i];
        }

        int avg = sum >> numPelLog2;
        for (int i = 0; i < w * h; i++)
        {
            acQ3[i] = reconQ3[i] - avg;
        }
    }

    /// <summary><c>cfl_predict_lbd_c</c>/<c>get_scaled_luma_q0</c>: <c>dst[i] = clip_pixel(round_power_of_two_signed(alpha_q3 * ac_q3[i], 6) + dst[i])</c>.</summary>
    public static void Predict(int[] acQ3, int[] chroma, int alphaQ3, int w, int h)
    {
        for (int i = 0; i < w * h; i++)
        {
            long scaledLumaQ6 = (long)alphaQ3 * acQ3[i];
            int scaledLuma = RoundPowerOfTwoSigned(scaledLumaQ6, 6);
            chroma[i] = Math.Clamp(scaledLuma + chroma[i], 0, 255);
        }
    }

    private static int RoundPowerOfTwoSigned(long value, int n) => value >= 0 ? (int)((value + (1L << (n - 1))) >> n) : -(int)((-value + (1L << (n - 1))) >> n);

    /// <summary>Runs the full real pipeline (subsample -&gt; subtract average -&gt; predict) end to end, the same composition <c>PredictChromaFromLuma</c>'s own single fused loop performs.</summary>
    public static void Run(int[] luma, int lumaStride, int lumaX0, int lumaY0, int[] chroma, int alphaQ3, int w, int h, int subX, int subY)
    {
        var reconQ3 = new int[w * h];
        Subsample(luma, lumaStride, lumaX0, lumaY0, reconQ3, w, h, subX, subY);

        var acQ3 = new int[w * h];
        SubtractAverage(reconQ3, acQ3, w, h);

        Predict(acQ3, chroma, alphaQ3, w, h);
    }
}
