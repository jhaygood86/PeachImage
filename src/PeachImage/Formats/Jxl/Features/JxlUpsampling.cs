using System.Numerics;
using System.Runtime.CompilerServices;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.Features;

/// <summary>
/// The 2x, 4x and 8x non-separable 5x5 upsampling filters of JPEG XL. Each output pixel is a weighted sum of the 5x5 input
/// neighbourhood, clamped to the range of that neighbourhood so the filter cannot overshoot.
/// </summary>
internal static class JxlUpsampling
{
    // Kernel weights for a top-left output quadrant, exploiting the filters' symmetry (see the format specification).
    public static readonly float[] DefaultWeights2 =
    [
        -0.01716200f, -0.03452303f, -0.04022174f, -0.02921014f, -0.00624645f,
        0.14111091f, 0.28896755f, 0.00278718f, -0.01610267f, 0.56661550f,
        0.03777607f, -0.01986694f, -0.03144731f, -0.01185068f, -0.00213539f,
    ];

    public static readonly float[] DefaultWeights4 =
    [
        -0.02419067f, -0.03491987f, -0.03693351f, -0.03094285f, -0.00529785f,
        -0.01663432f, -0.03556863f, -0.03888905f, -0.03516850f, -0.00989469f,
        0.23651958f, 0.33392945f, -0.01073543f, -0.01313181f, -0.03556694f,
        0.13048175f, 0.40103025f, 0.03951150f, -0.02077584f, 0.46914198f,
        -0.00209270f, -0.01484589f, -0.04064806f, 0.18942530f, 0.56279892f,
        0.06674400f, -0.02335494f, -0.03551682f, -0.00754830f, -0.02267919f,
        -0.02363578f, 0.00315804f, -0.03399098f, -0.01359519f, -0.00091653f,
        -0.00335467f, -0.01163294f, -0.01610294f, -0.00974088f, -0.00191622f,
        -0.01095446f, -0.03198464f, -0.04455121f, -0.02799790f, -0.00645912f,
        0.06390599f, 0.22963888f, 0.00630981f, -0.01897349f, 0.67537268f,
        0.08483369f, -0.02534994f, -0.02205197f, -0.01667999f, -0.00384443f,
    ];

    public static readonly float[] DefaultWeights8 =
    [
        -0.02928613f, -0.03706353f, -0.03783812f, -0.03324558f, -0.00447632f,
        -0.02519406f, -0.03752601f, -0.03901508f, -0.03663285f, -0.00646649f,
        -0.02066407f, -0.03838633f, -0.04002101f, -0.03900035f, -0.00901973f,
        -0.01626393f, -0.03954148f, -0.04046620f, -0.03979621f, -0.01224485f,
        0.29895328f, 0.35757708f, -0.02447552f, -0.01081748f, -0.04314594f,
        0.23903219f, 0.41119301f, -0.00573046f, -0.01450239f, -0.04246845f,
        0.17567618f, 0.45220643f, 0.02287757f, -0.01936783f, -0.03583255f,
        0.11572472f, 0.47416733f, 0.06284440f, -0.02685066f, 0.42720050f,
        -0.02248939f, -0.01155273f, -0.04562755f, 0.28689496f, 0.49093869f,
        -0.00007891f, -0.01545926f, -0.04562659f, 0.21238920f, 0.53980934f,
        0.03369474f, -0.02070211f, -0.03866988f, 0.14229550f, 0.56593398f,
        0.08045181f, -0.02888298f, -0.03680918f, -0.00542229f, -0.02920477f,
        -0.02788574f, -0.02118180f, -0.03942402f, -0.00775547f, -0.02433614f,
        -0.03193943f, -0.02030828f, -0.04044014f, -0.01074016f, -0.01930822f,
        -0.03620399f, -0.01974125f, -0.03919545f, -0.01456093f, -0.00045072f,
        -0.00360110f, -0.01020207f, -0.01231907f, -0.00638988f, -0.00071592f,
        -0.00279122f, -0.00957115f, -0.01288327f, -0.00730937f, -0.00107783f,
        -0.00210156f, -0.00890705f, -0.01317668f, -0.00813895f, -0.00153491f,
        -0.02128481f, -0.04173044f, -0.04831487f, -0.03293190f, -0.00525260f,
        -0.01720322f, -0.04052736f, -0.05045706f, -0.03607317f, -0.00738030f,
        -0.01341764f, -0.03965629f, -0.05151616f, -0.03814886f, -0.01005819f,
        0.18968273f, 0.33063684f, -0.01300105f, -0.01372950f, -0.04017465f,
        0.13727832f, 0.36402234f, 0.01027890f, -0.01832107f, -0.03365072f,
        0.08734506f, 0.38194295f, 0.04338228f, -0.02525993f, 0.56408126f,
        0.00458352f, -0.01648227f, -0.04887868f, 0.24585519f, 0.62026135f,
        0.04314807f, -0.02213737f, -0.04158014f, 0.16637289f, 0.65027023f,
        0.09621636f, -0.03101388f, -0.04082742f, -0.00904519f, -0.02790922f,
        -0.02117818f, 0.00798662f, -0.03995711f, -0.01243427f, -0.02231705f,
        -0.02946266f, 0.00992055f, -0.03600283f, -0.01684920f, -0.00111684f,
        -0.00411204f, -0.01297130f, -0.01723725f, -0.01022545f, -0.00165306f,
        -0.00313110f, -0.01218016f, -0.01763266f, -0.01125620f, -0.00231663f,
        -0.01374149f, -0.03797620f, -0.05142937f, -0.03117307f, -0.00581914f,
        -0.01064003f, -0.03608089f, -0.05272168f, -0.03375670f, -0.00795586f,
        0.09628104f, 0.27129991f, -0.00353779f, -0.01734151f, -0.03153981f,
        0.05686230f, 0.28500998f, 0.02230594f, -0.02374955f, 0.68214326f,
        0.05018048f, -0.02320852f, -0.04383616f, 0.18459474f, 0.71517975f,
        0.10805613f, -0.03263677f, -0.03637639f, -0.01394373f, -0.02511203f,
        -0.01728636f, 0.05407331f, -0.02867568f, -0.01893131f, -0.00240854f,
        -0.00446511f, -0.01636187f, -0.02377053f, -0.01522848f, -0.00333334f,
        -0.00819975f, -0.02964169f, -0.04499287f, -0.02745350f, -0.00612408f,
        0.02727416f, 0.19446600f, 0.00159832f, -0.02232473f, 0.74982506f,
        0.11452620f, -0.03348048f, -0.01605681f, -0.02070339f, -0.00458223f,
    ];

    public static float[] DefaultWeights(int shift) => shift switch
    {
        1 => DefaultWeights2,
        2 => DefaultWeights4,
        3 => DefaultWeights8,
        _ => throw new ArgumentOutOfRangeException(nameof(shift)),
    };

    /// <summary>Expands the symmetric weight table into one 5x5 kernel per output sub-pixel (N*N kernels of 25 weights).</summary>
    public static float[] BuildKernels(int shift, float[] weights)
    {
        int n = 1 << shift;
        int half = n / 2;
        var kernel = new float[n * n * 25];
        for (int ky = 0; ky < half; ky++)
        {
            for (int kx = 0; kx < half; kx++)
            {
                int offset0 = ((ky * n) + kx) * 25;
                int offset1 = ((ky * n) + (n - 1 - kx)) * 25;
                int offset2 = (((n - 1 - ky) * n) + kx) * 25;
                int offset3 = (((n - 1 - ky) * n) + (n - 1 - kx)) * 25;
                for (int py = 0; py < 5; py++)
                {
                    for (int px = 0; px < 5; px++)
                    {
                        int j = (5 * ky) + py;
                        int i = (5 * kx) + px;
                        int my = Math.Min(i, j);
                        int mx = Math.Max(i, j);
                        float w = weights[(5 * half * my) - (my * (my - 1) / 2) + mx - my];
                        kernel[offset0 + (py * 5) + px] = w;
                        kernel[offset1 + (py * 5) + (4 - px)] = w;
                        kernel[offset2 + ((4 - py) * 5) + px] = w;
                        kernel[offset3 + ((4 - py) * 5) + (4 - px)] = w;
                    }
                }
            }
        }

        return kernel;
    }

    /// <summary>
    /// Upsamples a <paramref name="width"/> x <paramref name="height"/> plane (rows <paramref name="srcStride"/> apart) by
    /// <c>2^shift</c>, mirroring at the plane's edges. The result has <c>width &lt;&lt; shift</c> columns per row
    /// (<paramref name="dstStride"/> floats apart).
    /// </summary>
    public static float[] Upsample(float[] source, int srcStride, int width, int height, int shift, float[] kernels, int dstStride, bool vectorized = true, float[]? destination = null)
    {
        int n = 1 << shift;
        var output = destination ?? new float[dstStride * height * n];
        var xs = new int[width + 4];
        for (int i = 0; i < xs.Length; i++)
        {
            xs[i] = Mirror(i - 2, width);
        }

        RowParallel.For(height, y => UpsampleRow(source, srcStride, width, height, y, shift, kernels, dstStride, output, xs, vectorized));
        return output;
    }

    private static void UpsampleRow(float[] source, int srcStride, int width, int height, int y, int shift, float[] kernels, int dstStride, float[] output, int[] xs, bool vectorized)
    {
        int n = 1 << shift;
        Span<int> rowOffsets = stackalloc int[5];
        for (int dy = 0; dy < 5; dy++)
        {
            rowOffsets[dy] = Mirror(y + dy - 2, height) * srcStride;
        }

        int x = 0;
        if (vectorized && Vector.IsHardwareAccelerated && width >= (2 * 2) + Vector<float>.Count + 1)
        {
            int lanes = Vector<float>.Count;
            Span<float> lanesOut = stackalloc float[n * lanes];
            Span<Vector<float>> window = stackalloc Vector<float>[25];
            for (; x < 2; x++)
            {
                UpsamplePixel(source, rowOffsets, x, y, n, kernels, dstStride, output, xs);
            }

            for (; x + lanes <= width - 2; x += lanes)
            {
                var min = Vector<float>.One * float.MaxValue;
                var max = Vector<float>.One * float.MinValue;
                for (int dy = 0; dy < 5; dy++)
                {
                    for (int dx = 0; dx < 5; dx++)
                    {
                        var v = new Vector<float>(source, rowOffsets[dy] + x + dx - 2);
                        window[(dy * 5) + dx] = v;
                        min = Vector.Min(min, v);
                        max = Vector.Max(max, v);
                    }
                }

                for (int oy = 0; oy < n; oy++)
                {
                    for (int ox = 0; ox < n; ox++)
                    {
                        int k = ((n * oy) + ox) * 25;
                        var acc0 = window[0] * kernels[k];
                        var acc1 = window[1] * kernels[k + 1];
                        var acc2 = window[2] * kernels[k + 2];
                        for (int i = 3; i < 24; i += 3)
                        {
                            acc0 = (window[i] * kernels[k + i]) + acc0;
                            acc1 = (window[i + 1] * kernels[k + i + 1]) + acc1;
                            acc2 = (window[i + 2] * kernels[k + i + 2]) + acc2;
                        }

                        acc0 = (window[24] * kernels[k + 24]) + acc0;
                        var result = Vector.Min(Vector.Max((acc1 + acc2) + acc0, min), max);
                        result.CopyTo(lanesOut.Slice(ox * lanes, lanes));
                    }

                    // Interleave the n sub-pixel vectors into the output row.
                    int dstRow = (((y * n) + oy) * dstStride) + (x * n);
                    for (int l = 0; l < lanes; l++)
                    {
                        for (int ox = 0; ox < n; ox++)
                        {
                            output[dstRow + (l * n) + ox] = lanesOut[(ox * lanes) + l];
                        }
                    }
                }
            }
        }

        for (; x < width; x++)
        {
            UpsamplePixel(source, rowOffsets, x, y, n, kernels, dstStride, output, xs);
        }
    }

    private static void UpsamplePixel(float[] source, ReadOnlySpan<int> rowOffsets, int x, int y, int n, float[] kernels, int dstStride, float[] output, int[] xs)
    {
        Span<float> window = stackalloc float[25];
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int dy = 0; dy < 5; dy++)
        {
            for (int dx = 0; dx < 5; dx++)
            {
                float v = source[rowOffsets[dy] + xs[x + dx]];
                window[(dy * 5) + dx] = v;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        }

        for (int oy = 0; oy < n; oy++)
        {
            int dstRow = ((y * n) + oy) * dstStride;
            for (int ox = 0; ox < n; ox++)
            {
                int k = ((n * oy) + ox) * 25;
                // Same summation grouping as the reference decoder (three interleaved accumulators).
                float acc0 = window[0] * kernels[k];
                float acc1 = window[1] * kernels[k + 1];
                float acc2 = window[2] * kernels[k + 2];
                for (int i = 3; i < 24; i += 3)
                {
                    acc0 = (window[i] * kernels[k + i]) + acc0;
                    acc1 = (window[i + 1] * kernels[k + i + 1]) + acc1;
                    acc2 = (window[i + 2] * kernels[k + i + 2]) + acc2;
                }

                acc0 = (window[24] * kernels[k + 24]) + acc0;
                float result = (acc1 + acc2) + acc0;
                output[dstRow + (x * n) + ox] = Math.Min(Math.Max(result, min), max);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mirror(int i, int size)
    {
        while (i < 0 || i >= size)
        {
            i = i < 0 ? -i - 1 : (2 * size) - i - 1;
        }

        return i;
    }
}
