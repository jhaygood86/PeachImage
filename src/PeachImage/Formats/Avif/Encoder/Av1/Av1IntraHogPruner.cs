using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Faithful port of libaom's real HOG (histogram-of-gradients)-based directional intra-mode pruning
/// (<c>prune_intra_mode_with_hog</c>, <c>av1/encoder/intra_mode_search_utils.h</c>, read directly from the
/// local checkout at <c>C:\Sources\GoogleSource\aom</c>) -- a cheap, block-local pre-filter that removes some
/// of the 8 directional intra modes from consideration entirely, before either the real RD search or even
/// the cheaper SATD-based shortlist (<c>TopIntraModelCountAllowed</c>) ever runs. Confirmed via direct source
/// reading that this is a hard pre-filter, not a score adjustment: a masked-out directional mode is skipped
/// outright (<c>continue</c> in libaom's own mode loop), independent of and strictly before any SATD/model-RD
/// scoring.
///
/// <para>The "model" is a small, pre-trained <em>linear</em> model (libaom's own <c>NN_CONFIG</c> machinery
/// with <c>num_hidden_layers = 0</c> -- confirmed by reading <c>av1/encoder/ml.c</c>'s <c>av1_nn_predict_c</c>:
/// with zero hidden layers, the hidden-layer loop never executes, leaving a single unactivated
/// (no ReLU/softmax) output layer, i.e. <c>scores = W\u00b7hist + b</c>, a plain dot product), not a real
/// multi-layer network despite the name -- <see cref="Weights"/>/<see cref="Bias"/> are libaom's own
/// pre-trained <c>av1_intra_hog_model_weights</c>/<c>_bias</c> constants, reproduced verbatim.</para>
///
/// <para>Ported the plain C reference (<c>av1_nn_predict_c</c>), not libaom's SIMD-dispatched variants
/// (<c>av1_nn_predict_sse3</c>/<c>_avx2</c>/neon, selected at runtime on real hardware) -- confirmed those use
/// a different floating-point summation order (SIMD horizontal-add tree vs. this port's sequential
/// accumulation), which can differ from the C reference by up to one ULP before the final quantization step
/// absorbs it. This project's own Phase 0 harness reference (<c>aomenc.exe</c>, <c>build_ninja2</c>) is built
/// with <c>-DAOM_TARGET_CPU=generic</c> -- no SIMD, pure C -- so it always runs <c>av1_nn_predict_c</c>
/// itself; porting the SIMD variant would target hardware this project's own comparison harness never
/// actually exercises.</para>
/// </summary>
internal static class Av1IntraHogPruner
{
    private const int Bins = 32;
    private const int DirectionalModes = 8;

    /// <summary>
    /// Per-<c>IntraPruningWithHog</c>/<c>ChromaIntraPruningWithHog</c> level threshold (index = level - 1), a
    /// mode is pruned when its predicted score is <c>&lt;=</c> this value. libaom keeps two rows (interframe
    /// vs. intraframe, <c>intra_mode_search.c</c>'s two <c>thresh[2][4]</c>/<c>thresh[4]</c> tables) that
    /// happen to be numerically identical for the intraframe case this encoder always is (AVIF has no inter
    /// frames) -- both luma's own dedicated table and chroma's own intraframe row are exactly
    /// <c>{ -1.2, -1.2, -0.6, 0.4 }</c>, so one shared table suffices here.
    /// </summary>
    public static readonly float[] Thresh = [-1.2f, -1.2f, -0.6f, 0.4f];

    /// <summary><c>av1_intra_hog_model_bias</c> (<c>intra_mode_search_utils.h:41-44</c>), one bias per directional mode, in V/H/D45/D135/D113/D157/D203/D67 order.</summary>
    private static readonly float[] Bias =
    [
        0.450578f, 0.695518f, -0.717944f, -0.639894f,
        -0.602019f, -0.453454f, 0.055857f, -0.465480f,
    ];

    /// <summary><c>av1_intra_hog_model_weights</c> (<c>intra_mode_search_utils.h:46-90</c>), row-major by output node: <c>Weights[node * Bins + bin]</c>, 8 rows of 32 floats.</summary>
    private static readonly float[] Weights =
    [
        -3.076402f, -3.757063f, -3.275266f, -3.180665f, -3.452105f, -3.216593f,
        -2.871212f, -3.134296f, -1.822324f, -2.401411f, -1.541016f, -1.195322f,
        -0.434156f, 0.322868f, 2.260546f, 3.368715f, 3.989290f, 3.308487f,
        2.277893f, 0.923793f, 0.026412f, -0.385174f, -0.718622f, -1.408867f,
        -1.050558f, -2.323941f, -2.225827f, -2.585453f, -3.054283f, -2.875087f,
        -2.985709f, -3.447155f, 3.758139f, 3.204353f, 2.170998f, 0.826587f,
        -0.269665f, -0.702068f, -1.085776f, -2.175249f, -1.623180f, -2.975142f,
        -2.779629f, -3.190799f, -3.521900f, -3.375480f, -3.319355f, -3.897389f,
        -3.172334f, -3.594528f, -2.879132f, -2.547777f, -2.921023f, -2.281844f,
        -1.818988f, -2.041771f, -0.618268f, -1.396458f, -0.567153f, -0.285868f,
        -0.088058f, 0.753494f, 2.092413f, 3.215266f, -3.300277f, -2.748658f,
        -2.315784f, -2.423671f, -2.257283f, -2.269583f, -2.196660f, -2.301076f,
        -2.646516f, -2.271319f, -2.254366f, -2.300102f, -2.217960f, -2.473300f,
        -2.116866f, -2.528246f, -3.314712f, -1.701010f, -0.589040f, -0.088077f,
        0.813112f, 1.702213f, 2.653045f, 3.351749f, 3.243554f, 3.199409f,
        2.437856f, 1.468854f, 0.533039f, -0.099065f, -0.622643f, -2.200732f,
        -4.228861f, -2.875263f, -1.273956f, -0.433280f, 0.803771f, 1.975043f,
        3.179528f, 3.939064f, 3.454379f, 3.689386f, 3.116411f, 1.970991f,
        0.798406f, -0.628514f, -1.252546f, -2.825176f, -4.090178f, -3.777448f,
        -3.227314f, -3.479403f, -3.320569f, -3.159372f, -2.729202f, -2.722341f,
        -3.054913f, -2.742923f, -2.612703f, -2.662632f, -2.907314f, -3.117794f,
        -3.102660f, -3.970972f, -4.891357f, -3.935582f, -3.347758f, -2.721924f,
        -2.219011f, -1.702391f, -0.866529f, -0.153743f, 0.107733f, 1.416882f,
        2.572884f, 3.607755f, 3.974820f, 3.997783f, 2.970459f, 0.791687f,
        -1.478921f, -1.228154f, -1.216955f, -1.765932f, -1.951003f, -1.985301f,
        -1.975881f, -1.985593f, -2.422371f, -2.419978f, -2.531288f, -2.951853f,
        -3.071380f, -3.277027f, -3.373539f, -4.462010f, -0.967888f, 0.805524f,
        2.794130f, 3.685984f, 3.745195f, 3.252444f, 2.316108f, 1.399146f,
        -0.136519f, -0.162811f, -1.004357f, -1.667911f, -1.964662f, -2.937579f,
        -3.019533f, -3.942766f, -5.102767f, -3.882073f, -3.532027f, -3.451956f,
        -2.944015f, -2.643064f, -2.529872f, -2.077290f, -2.809965f, -1.803734f,
        -1.783593f, -1.662585f, -1.415484f, -1.392673f, -0.788794f, -1.204819f,
        -1.998864f, -1.182102f, -0.892110f, -1.317415f, -1.359112f, -1.522867f,
        -1.468552f, -1.779072f, -2.332959f, -2.160346f, -2.329387f, -2.631259f,
        -2.744936f, -3.052494f, -2.787363f, -3.442548f, -4.245075f, -3.032172f,
        -2.061609f, -1.768116f, -1.286072f, -0.706587f, -0.192413f, 0.386938f,
        0.716997f, 1.481393f, 2.216702f, 2.737986f, 3.109809f, 3.226084f,
        2.490098f, -0.095827f, -3.864816f, -3.507248f, -3.128925f, -2.908251f,
        -2.883836f, -2.881411f, -2.524377f, -2.624478f, -2.399573f, -2.367718f,
        -1.918255f, -1.926277f, -1.694584f, -1.723790f, -0.966491f, -1.183115f,
        -1.430687f, 0.872896f, 2.766550f, 3.610080f, 3.578041f, 3.334928f,
        2.586680f, 1.895721f, 1.122195f, 0.488519f, -0.140689f, -0.799076f,
        -1.222860f, -1.502437f, -1.900969f, -3.206816f,
    ];

    /// <summary>
    /// <c>get_hist_bin_idx</c>'s bin-boundary table (<c>intra_mode_search_utils.h:110-115</c>) -- 32 ascending
    /// thresholds on a fixed-point (1&lt;&lt;16) <c>dy/dx</c> ratio; the bin is the first index whose threshold
    /// is <c>&gt;=</c> the ratio. libaom scans this in blocks of 8 for its own performance reasons (its own
    /// comment: "gives better performance than binary search here") -- a plain linear scan from index 0
    /// returns the identical first match, so that micro-optimization isn't reproduced here.
    /// </summary>
    private static readonly int[] Thresholds =
    [
        -1334015, -441798, -261605, -183158, -138560, -109331, -88359, -72303,
        -59392, -48579, -39272, -30982, -23445, -16400, -9715, -3194,
        3227, 9748, 16433, 23478, 31015, 39305, 48611, 59425,
        72336, 88392, 109364, 138593, 183191, 261638, 441831, int.MaxValue,
    ];

    /// <summary>
    /// <c>prune_intra_mode_with_hog</c>: computes a 32-bin gradient histogram from
    /// <paramref name="source"/>'s own <paramref name="widthPixels"/>x<paramref name="heightPixels"/> region
    /// at (<paramref name="x"/>, <paramref name="y"/>) (stride <paramref name="stride"/>), predicts a score
    /// for each of the 8 directional modes via the linear model above, and marks
    /// <paramref name="directionalModeSkipMask"/>'s corresponding <see cref="Av1IntraMode"/> entry
    /// (<c>VPred</c>..<c>D67Pred</c>) when that mode's score is <c>&lt;= threshold</c>. Mirrors libaom's own
    /// call shape exactly: computed once per block, purely from that block's own source pixels (no
    /// neighbor/reconstruction dependency at all -- <c>collect_hog_data</c>'s own Sobel taps never read past
    /// the block's own footprint), so this can run against any candidate block regardless of what has or
    /// hasn't been encoded elsewhere yet.
    /// </summary>
    /// <param name="source">Row-major plane samples (luma or one chroma plane), stride <paramref name="stride"/>.</param>
    /// <param name="stride">The plane's own row stride in samples.</param>
    /// <param name="x">The candidate block's left edge, in the same pixel coordinates as <paramref name="source"/>.</param>
    /// <param name="y">The candidate block's top edge.</param>
    /// <param name="widthPixels">The candidate block's width in pixels.</param>
    /// <param name="heightPixels">The candidate block's height in pixels.</param>
    /// <param name="threshold">The effort-indexed <see cref="Thresh"/> entry for this call's plane.</param>
    /// <param name="chromaSubsamplingScale">
    /// libaom's own luma/chroma HOG-magnitude parity scale, <c>(1 + subsampling_x) * (1 + subsampling_y)</c>
    /// (<c>collect_hog_data</c>) -- 1 for luma or any 4:4:4 chroma plane (this encoder's own lossless chroma
    /// is always 4:4:4, see <see cref="Av1TileEncoder"/>'s own <c>chromaSubX</c>/<c>chromaSubY</c> remarks, so
    /// this is always a no-op here in practice), 4 for a real 4:2:0 chroma plane. Applied uniformly to every
    /// histogram bin before the model runs, exactly like libaom's own <c>for (b) hog[b] *= scale;</c>.
    /// </param>
    /// <param name="directionalModeSkipMask">Output mask, indexed by <see cref="Av1IntraMode"/> value -- only entries <c>VPred</c>..<c>D67Pred</c> are ever written.</param>
    public static void ComputeSkipMask(int[] source, int stride, int x, int y, int widthPixels, int heightPixels, float threshold, int chromaSubsamplingScale, Span<bool> directionalModeSkipMask)
    {
        Span<float> hist = stackalloc float[Bins];
        GenerateHog(source, stride, x, y, widthPixels, heightPixels, hist);
        if (chromaSubsamplingScale != 1)
        {
            for (int i = 0; i < Bins; i++)
            {
                hist[i] *= chromaSubsamplingScale;
            }
        }

        Span<float> scores = stackalloc float[DirectionalModes];
        Predict(hist, scores);
        for (int i = 0; i < DirectionalModes; i++)
        {
            if (scores[i] <= threshold)
            {
                directionalModeSkipMask[Av1IntraMode.VPred + i] = true;
            }
        }
    }

    /// <summary><c>lowbd_generate_hog</c> (<c>intra_mode_search_utils.h:149-181</c>): a 3x3 Sobel gradient over the block's own interior pixels (row/col 0 and the last row/col are never a Sobel center, matching libaom's own <c>r/c in [1, rows/cols - 2]</c> loop bounds), binned by direction and normalized by total gradient magnitude.</summary>
    internal static void GenerateHog(int[] source, int stride, int x, int y, int width, int height, Span<float> hist)
    {
        float total = 0.1f;
        for (int r = 1; r < height - 1; r++)
        {
            int rowBase = ((y + r) * stride) + x;
            for (int c = 1; c < width - 1; c++)
            {
                int idx = rowBase + c;
                int above = source[idx - stride];
                int below = source[idx + stride];
                int left = source[idx - 1];
                int right = source[idx + 1];
                int aboveLeft = source[idx - stride - 1];
                int aboveRight = source[idx - stride + 1];
                int belowLeft = source[idx + stride - 1];
                int belowRight = source[idx + stride + 1];

                int dx = (aboveRight + (2 * right) + belowRight) - (aboveLeft + (2 * left) + belowLeft);
                int dy = (belowLeft + (2 * below) + belowRight) - (aboveLeft + (2 * above) + aboveRight);
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int temp = Math.Abs(dx) + Math.Abs(dy);
                if (temp == 0)
                {
                    continue;
                }

                total += temp;
                if (dx == 0)
                {
                    // Integer division, matching libaom's own C `temp / 2` exactly -- an odd temp loses its
                    // low bit once here (added to both bins separately), not a rounding choice to improve on.
                    hist[0] += temp / 2;
                    hist[Bins - 1] += temp / 2;
                }
                else
                {
                    int binIdx = GetHistBinIdx(dx, dy);
                    hist[binIdx] += temp;
                }
            }
        }

        for (int i = 0; i < Bins; i++)
        {
            hist[i] /= total;
        }
    }

    /// <summary><c>get_hist_bin_idx</c>: a fixed-point (1&lt;&lt;16) <c>dy/dx</c> ratio (C-style truncating integer division, reproduced exactly by C#'s own <c>int / int</c>) located in <see cref="Thresholds"/> by linear scan.</summary>
    internal static int GetHistBinIdx(int dx, int dy)
    {
        int ratio = (dy * (1 << 16)) / dx;
        for (int idx = 0; idx < Bins; idx++)
        {
            if (ratio <= Thresholds[idx])
            {
                return idx;
            }
        }

        return Bins - 1;
    }

    /// <summary>
    /// <c>av1_nn_predict_c</c> specialized to <c>av1_intra_hog_model_nnconfig</c>'s own shape (0 hidden
    /// layers -- so this is just <c>scores = Weights\u00b7hist + Bias</c>, no activation at all, not even on
    /// what would otherwise be a hidden layer) followed by <c>av1_nn_output_prec_reduce</c>'s 1/512-precision
    /// quantization (<c>reduce_prec = 1</c> at this call site, <c>ml.c:19-26</c>) -- <c>(int)(x * 512 + 0.5)</c>
    /// is a C truncating cast, reproduced as-is (not <c>Math.Round</c>, which would round negative half-way
    /// values differently) since C#'s own <c>(int)</c> cast on a <c>float</c> truncates toward zero exactly
    /// like C's.
    /// </summary>
    internal static void Predict(ReadOnlySpan<float> hist, Span<float> scores)
    {
        const int prec = 512;
        for (int node = 0; node < DirectionalModes; node++)
        {
            float val = Bias[node];
            int rowBase = node * Bins;
            for (int i = 0; i < Bins; i++)
            {
                val += Weights[rowBase + i] * hist[i];
            }

            scores[node] = (int)((val * prec) + 0.5f) / (float)prec;
        }
    }
}
