using System.Collections.Concurrent;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>
/// The inverse transforms of VarDCT: scaled inverse DCTs of every block shape, the specialised small transforms (identity,
/// 2x2, 4x4, 4x8/8x4, AFV), and the forward DCT used to derive the lowest-frequency coefficients of large blocks from DC values.
/// </summary>
/// <remarks>
/// JPEG XL's DCT convention is unnormalised: the 1-D inverse is <c>x[n] = c[0] + sqrt(2) * sum_k c[k] cos((n + 1/2) k pi / N)</c>
/// and the forward transform divides by N. Coefficient blocks are stored with the smaller dimension as rows; for a block at
/// least as tall as wide the stored matrix is indexed [horizontal frequency][vertical frequency], otherwise [vertical][horizontal].
/// </remarks>
internal static class DctTransforms
{
    private const float Sqrt2 = 1.41421356237f;

    private static readonly ConcurrentDictionary<int, float[]> Bases = new();

    // Basis of the AFV 4x4 transform: pixel[i] = sum_j coeff[j] * basis[j][i].
    private static readonly float[] AfvBasis =
    [
        0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f,
        0.876902929799142f, 0.2206518106944235f, -0.10140050393753763f, -0.1014005039375375f, 0.2206518106944236f, -0.10140050393753777f, -0.10140050393753772f, -0.10140050393753763f, -0.10140050393753758f, -0.10140050393753769f, -0.1014005039375375f, -0.10140050393753768f, -0.10140050393753768f, -0.10140050393753759f, -0.10140050393753763f, -0.10140050393753741f,
        0.0f, 0.0f, 0.40670075830260755f, 0.44444816619734445f, 0.0f, 0.0f, 0.19574399372042936f, 0.2929100136981264f, -0.40670075830260716f, -0.19574399372042872f, 0.0f, 0.11379074460448091f, -0.44444816619734384f, -0.29291001369812636f, -0.1137907446044814f, 0.0f,
        0.0f, 0.0f, -0.21255748058288748f, 0.3085497062849767f, 0.0f, 0.4706702258572536f, -0.1621205195722993f, 0.0f, -0.21255748058287047f, -0.16212051957228327f, -0.47067022585725277f, -0.1464291867126764f, 0.3085497062849487f, 0.0f, -0.14642918671266536f, 0.4251149611657548f,
        0.0f, -0.7071067811865474f, 0.0f, 0.0f, 0.7071067811865476f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f,
        -0.4105377591765233f, 0.6235485373547691f, -0.06435071657946274f, -0.06435071657946266f, 0.6235485373547694f, -0.06435071657946284f, -0.0643507165794628f, -0.06435071657946274f, -0.06435071657946272f, -0.06435071657946279f, -0.06435071657946266f, -0.06435071657946277f, -0.06435071657946277f, -0.06435071657946273f, -0.06435071657946274f, -0.0643507165794626f,
        0.0f, 0.0f, -0.4517556589999482f, 0.15854503551840063f, 0.0f, -0.04038515160822202f, 0.0074182263792423875f, 0.39351034269210167f, -0.45175565899994635f, 0.007418226379244351f, 0.1107416575309343f, 0.08298163094882051f, 0.15854503551839705f, 0.3935103426921022f, 0.0829816309488214f, -0.45175565899994796f,
        0.0f, 0.0f, -0.304684750724869f, 0.5112616136591823f, 0.0f, 0.0f, -0.290480129728998f, -0.06578701549142804f, 0.304684750724884f, 0.2904801297290076f, 0.0f, -0.23889773523344604f, -0.5112616136592012f, 0.06578701549142545f, 0.23889773523345467f, 0.0f,
        0.0f, 0.0f, 0.3017929516615495f, 0.25792362796341184f, 0.0f, 0.16272340142866204f, 0.09520022653475037f, 0.0f, 0.3017929516615503f, 0.09520022653475055f, -0.16272340142866173f, -0.35312385449816297f, 0.25792362796341295f, 0.0f, -0.3531238544981624f, -0.6035859033230976f,
        0.0f, 0.0f, 0.40824829046386274f, 0.0f, 0.0f, 0.0f, 0.0f, -0.4082482904638628f, -0.4082482904638635f, 0.0f, 0.0f, -0.40824829046386296f, 0.0f, 0.4082482904638634f, 0.408248290463863f, 0.0f,
        0.0f, 0.0f, 0.1747866975480809f, 0.0812611176717539f, 0.0f, 0.0f, -0.3675398009862027f, -0.307882213957909f, -0.17478669754808135f, 0.3675398009862011f, 0.0f, 0.4826689115059883f, -0.08126111767175039f, 0.30788221395790305f, -0.48266891150598584f, 0.0f,
        0.0f, 0.0f, -0.21105601049335784f, 0.18567180916109802f, 0.0f, 0.0f, 0.49215859013738733f, -0.38525013709251915f, 0.21105601049335806f, -0.49215859013738905f, 0.0f, 0.17419412659916217f, -0.18567180916109904f, 0.3852501370925211f, -0.1741941265991621f, 0.0f,
        0.0f, 0.0f, -0.14266084808807264f, -0.3416446842253372f, 0.0f, 0.7367497537172237f, 0.24627107722075148f, -0.08574019035519306f, -0.14266084808807344f, 0.24627107722075137f, 0.14883399227113567f, -0.04768680350229251f, -0.3416446842253373f, -0.08574019035519267f, -0.047686803502292804f, -0.14266084808807242f,
        0.0f, 0.0f, -0.13813540350758585f, 0.3302282550303788f, 0.0f, 0.08755115000587084f, -0.07946706605909573f, -0.4613374887461511f, -0.13813540350758294f, -0.07946706605910261f, 0.49724647109535086f, 0.12538059448563663f, 0.3302282550303805f, -0.4613374887461554f, 0.12538059448564315f, -0.13813540350758452f,
        0.0f, 0.0f, -0.17437602599651067f, 0.0702790691196284f, 0.0f, -0.2921026642334881f, 0.3623817333531167f, 0.0f, -0.1743760259965108f, 0.36238173335311646f, 0.29210266423348785f, -0.4326608024727445f, 0.07027906911962818f, 0.0f, -0.4326608024727457f, 0.34875205199302267f,
        0.0f, 0.0f, 0.11354987314994337f, -0.07417504595810355f, 0.0f, 0.19402893032594343f, -0.435190496523228f, 0.21918684838857466f, 0.11354987314994257f, -0.4351904965232251f, 0.5550443808910661f, -0.25468277124066463f, -0.07417504595810233f, 0.2191868483885728f, -0.25468277124066413f, 0.1135498731499429f,
    ];

    // B[n * size + k] = (k == 0 ? 1 : sqrt(2) * cos((n + 1/2) k pi / size)).
    private static float[] Basis(int size) => Bases.GetOrAdd(size, static n =>
    {
        var basis = new float[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < n; k++)
            {
                basis[(i * n) + k] = k == 0 ? 1f : (float)(Math.Sqrt(2.0) * Math.Cos((i + 0.5) * k * Math.PI / n));
            }
        }

        return basis;
    });

    /// <summary>
    /// The scaled inverse DCT of a <paramref name="rows"/> x <paramref name="cols"/> pixel block. <paramref name="from"/> holds
    /// the coefficients in the stored layout and is not modified; the pixels are written to <paramref name="to"/> with the given stride.
    /// </summary>
    public static void ScaledIdct(int rows, int cols, ReadOnlySpan<float> from, Span<float> to, int toStride, Span<float> scratch)
    {
        int area = rows * cols;
        var block = scratch[..area];
        var block2 = scratch.Slice(area, area);
        var work = scratch[(2 * area)..];

        if (rows < cols)
        {
            // Stored [rows][cols]: transpose, 1-D IDCT along the columns axis, transpose, 1-D IDCT along the rows axis.
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    block[(c * rows) + r] = from[(r * cols) + c];
                }
            }

            // block2 (cols x rows) = B_cols * block.
            FastIdct.Transform(cols, block, block2, rows, rows, work);

            // Transpose into block (rows x cols).
            for (int n = 0; n < cols; n++)
            {
                for (int j = 0; j < rows; j++)
                {
                    block[(j * cols) + n] = block2[(n * rows) + j];
                }
            }

            FastIdct.Transform(rows, block, to, toStride, cols, work);
        }
        else
        {
            // Stored [cols][rows]: IDCT along the first axis, transpose, IDCT along the other.
            FastIdct.Transform(cols, from, block2, rows, rows, work);
            for (int n = 0; n < cols; n++)
            {
                for (int j = 0; j < rows; j++)
                {
                    block[(j * cols) + n] = block2[(n * rows) + j];
                }
            }

            FastIdct.Transform(rows, block, to, toStride, cols, work);
        }
    }

    /// <summary>
    /// The scaled forward DCT of a <paramref name="rows"/> x <paramref name="cols"/> grid (stride <paramref name="fromStride"/>),
    /// in the same stored layout <see cref="ScaledIdct"/> reads: for rows &lt; cols a [rows][cols] matrix, otherwise [cols][rows].
    /// </summary>
    private static void ScaledDct(int rows, int cols, ReadOnlySpan<float> from, int fromStride, Span<float> result)
    {
        var basisRows = Basis(rows);
        var basisCols = Basis(cols);

        // Step 1: DCT along the rows axis for every column: first[k][c] = 1/R * sum_r B_R[r][k] * from[r][c].
        var first = new float[rows * cols];
        for (int k = 0; k < rows; k++)
        {
            for (int c = 0; c < cols; c++)
            {
                float sum = 0;
                for (int r = 0; r < rows; r++)
                {
                    sum += basisRows[(r * rows) + k] * from[(r * fromStride) + c];
                }

                first[(k * cols) + c] = sum / rows;
            }
        }

        // Step 2: DCT along the columns axis: second[k'][k] = 1/C * sum_c B_C[c][k'] * first[k][c].
        var second = new float[cols * rows];
        for (int kp = 0; kp < cols; kp++)
        {
            for (int k = 0; k < rows; k++)
            {
                float sum = 0;
                for (int c = 0; c < cols; c++)
                {
                    sum += basisCols[(c * cols) + kp] * first[(k * cols) + c];
                }

                second[(kp * rows) + k] = sum / cols;
            }
        }

        if (rows < cols)
        {
            // [rows][cols] layout.
            for (int k = 0; k < rows; k++)
            {
                for (int kp = 0; kp < cols; kp++)
                {
                    result[(k * cols) + kp] = second[(kp * rows) + k];
                }
            }
        }
        else
        {
            // [cols][rows] layout.
            second.CopyTo(result);
        }
    }

    // 1 / (cos(i pi / (2N)) cos(i pi / N) cos(2 i pi / N)): scales the i-th coefficient of a small DCT to the matching N-point one.
    private static float ResampleScale(int i, int n) =>
        (float)(1.0 / (Math.Cos(i * Math.PI / (2.0 * n)) * Math.Cos(i * Math.PI / n) * Math.Cos(i * Math.PI / (n / 2.0))));

    /// <summary>
    /// Fills the lowest-frequency coefficients (the top-left <c>coveredY x coveredX</c> region) of a large block from the DC values of
    /// the blocks it covers. <paramref name="dc"/> is a plane of DC values with the given stride, positioned at the block's first DC.
    /// </summary>
    public static void LowestFrequenciesFromDc(int strategy, ReadOnlySpan<float> dc, int dcStride, Span<float> llf)
    {
        int cx = AcStrategy.CoveredBlocksX(strategy);
        int cy = AcStrategy.CoveredBlocksY(strategy);
        if (cx * cy == 1)
        {
            llf[0] = dc[0];
            return;
        }

        int lfRows = cy;
        int lfCols = cx;
        int dctRows = cy * 8;
        int dctCols = cx * 8;
        int outStride = Math.Max(cx, cy) * 8;
        var block = new float[lfRows * lfCols];
        ScaledDct(lfRows, lfCols, dc, dcStride, block);

        if (lfRows < lfCols)
        {
            for (int y = 0; y < lfRows; y++)
            {
                for (int x = 0; x < lfCols; x++)
                {
                    llf[(y * outStride) + x] = block[(y * lfCols) + x] * ResampleScale(y, dctRows) * ResampleScale(x, dctCols);
                }
            }
        }
        else
        {
            for (int y = 0; y < lfCols; y++)
            {
                for (int x = 0; x < lfRows; x++)
                {
                    llf[(y * outStride) + x] = block[(y * lfRows) + x] * ResampleScale(y, dctCols) * ResampleScale(x, dctRows);
                }
            }
        }
    }

    /// <summary>
    /// Transforms the dequantized coefficients of one block (one channel) to pixels. <paramref name="coefficients"/> may be modified;
    /// <paramref name="scratch"/> must hold at least <c>5 * rows * cols</c> floats.
    /// </summary>
    public static void TransformToPixels(int strategy, Span<float> coefficients, Span<float> pixels, int stride, Span<float> scratch)
    {
        var type = (AcStrategyType)strategy;
        switch (type)
        {
            case AcStrategyType.Identity:
                Identity(coefficients, pixels, stride);
                break;
            case AcStrategyType.Dct8x4:
            {
                float b0 = coefficients[0];
                float b1 = coefficients[8];
                Span<float> dcs = [b0 + b1, b0 - b1];
                Span<float> block = stackalloc float[4 * 8];
                for (int x = 0; x < 2; x++)
                {
                    block[0] = dcs[x];
                    for (int iy = 0; iy < 4; iy++)
                    {
                        for (int ix = 0; ix < 8; ix++)
                        {
                            if (ix == 0 && iy == 0)
                            {
                                continue;
                            }

                            block[(iy * 8) + ix] = coefficients[((x + (iy * 2)) * 8) + ix];
                        }
                    }

                    ScaledIdct(8, 4, block, pixels[(x * 4)..], stride, scratch);
                }

                break;
            }

            case AcStrategyType.Dct4x8:
            {
                float b0 = coefficients[0];
                float b1 = coefficients[8];
                Span<float> dcs = [b0 + b1, b0 - b1];
                Span<float> block = stackalloc float[4 * 8];
                for (int y = 0; y < 2; y++)
                {
                    block[0] = dcs[y];
                    for (int iy = 0; iy < 4; iy++)
                    {
                        for (int ix = 0; ix < 8; ix++)
                        {
                            if (ix == 0 && iy == 0)
                            {
                                continue;
                            }

                            block[(iy * 8) + ix] = coefficients[((y + (iy * 2)) * 8) + ix];
                        }
                    }

                    ScaledIdct(4, 8, block, pixels[(y * 4 * stride)..], stride, scratch);
                }

                break;
            }

            case AcStrategyType.Dct4x4:
            {
                float b00 = coefficients[0];
                float b01 = coefficients[1];
                float b10 = coefficients[8];
                float b11 = coefficients[9];
                Span<float> dcs =
                [
                    b00 + b01 + b10 + b11,
                    b00 + b01 - b10 - b11,
                    b00 - b01 + b10 - b11,
                    b00 - b01 - b10 + b11,
                ];
                Span<float> block = stackalloc float[16];
                for (int y = 0; y < 2; y++)
                {
                    for (int x = 0; x < 2; x++)
                    {
                        block[0] = dcs[(y * 2) + x];
                        for (int iy = 0; iy < 4; iy++)
                        {
                            for (int ix = 0; ix < 4; ix++)
                            {
                                if (ix == 0 && iy == 0)
                                {
                                    continue;
                                }

                                block[(iy * 4) + ix] = coefficients[((y + (iy * 2)) * 8) + x + (ix * 2)];
                            }
                        }

                        ScaledIdct(4, 4, block, pixels[((y * 4 * stride) + (x * 4))..], stride, scratch);
                    }
                }

                break;
            }

            case AcStrategyType.Dct2x2:
            {
                Span<float> coeffs = stackalloc float[64];
                coefficients[..64].CopyTo(coeffs);
                Idct2TopBlock(2, coeffs);
                Idct2TopBlock(4, coeffs);
                Idct2TopBlock(8, coeffs);
                for (int y = 0; y < 8; y++)
                {
                    coeffs.Slice(y * 8, 8).CopyTo(pixels.Slice(y * stride, 8));
                }

                break;
            }

            case AcStrategyType.Afv0:
            case AcStrategyType.Afv1:
            case AcStrategyType.Afv2:
            case AcStrategyType.Afv3:
                AfvTransformToPixels(strategy - (int)AcStrategyType.Afv0, coefficients, pixels, stride, scratch);
                break;
            default:
            {
                int rows = AcStrategy.CoveredBlocksY(strategy) * 8;
                int cols = AcStrategy.CoveredBlocksX(strategy) * 8;
                ScaledIdct(rows, cols, coefficients, pixels, stride, scratch);
                break;
            }
        }
    }

    private static void Identity(Span<float> coefficients, Span<float> pixels, int stride)
    {
        float block00 = coefficients[0];
        float block01 = coefficients[1];
        float block10 = coefficients[8];
        float block11 = coefficients[9];
        Span<float> dcs =
        [
            block00 + block01 + block10 + block11,
            block00 + block01 - block10 - block11,
            block00 - block01 + block10 - block11,
            block00 - block01 - block10 + block11,
        ];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                float blockDc = dcs[(y * 2) + x];
                float residualSum = 0;
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        if (ix == 0 && iy == 0)
                        {
                            continue;
                        }

                        residualSum += coefficients[((y + (iy * 2)) * 8) + x + (ix * 2)];
                    }
                }

                int centre = (((4 * y) + 1) * stride) + (4 * x) + 1;
                pixels[centre] = blockDc - (residualSum * (1.0f / 16));
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        if (ix == 1 && iy == 1)
                        {
                            continue;
                        }

                        pixels[(((y * 4) + iy) * stride) + (x * 4) + ix] = coefficients[((y + (iy * 2)) * 8) + x + (ix * 2)] + pixels[centre];
                    }
                }

                pixels[(y * 4 * stride) + (x * 4)] = coefficients[((y + 2) * 8) + x + 2] + pixels[centre];
            }
        }
    }

    // One level of the 2x2 "Haar-like" inverse: expands the top-left SxS block in place using 8-wide rows.
    private static void Idct2TopBlock(int s, Span<float> block)
    {
        Span<float> temp = stackalloc float[64];
        int num2x2 = s / 2;
        for (int y = 0; y < num2x2; y++)
        {
            for (int x = 0; x < num2x2; x++)
            {
                float c00 = block[(y * 8) + x];
                float c01 = block[(y * 8) + num2x2 + x];
                float c10 = block[((y + num2x2) * 8) + x];
                float c11 = block[((y + num2x2) * 8) + num2x2 + x];
                temp[(y * 2 * 8) + (x * 2)] = c00 + c01 + c10 + c11;
                temp[(y * 2 * 8) + (x * 2) + 1] = c00 + c01 - c10 - c11;
                temp[(((y * 2) + 1) * 8) + (x * 2)] = c00 - c01 + c10 - c11;
                temp[(((y * 2) + 1) * 8) + (x * 2) + 1] = c00 - c01 - c10 + c11;
            }
        }

        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                block[(y * 8) + x] = temp[(y * 8) + x];
            }
        }
    }

    private static void AfvIdct4x4(ReadOnlySpan<float> coeffs, Span<float> pixels)
    {
        for (int i = 0; i < 16; i++)
        {
            float pixel = 0;
            for (int j = 0; j < 16; j++)
            {
                pixel += coeffs[j] * AfvBasis[(j * 16) + i];
            }

            pixels[i] = pixel;
        }
    }

    private static void AfvTransformToPixels(int kind, Span<float> coefficients, Span<float> pixels, int stride, Span<float> scratch)
    {
        int afvX = kind & 1;
        int afvY = kind / 2;
        float block00 = coefficients[0];
        float block01 = coefficients[1];
        float block10 = coefficients[8];
        Span<float> dcs = [(block00 + block10 + block01) * 4.0f, block00 + block10 - block01, block00 - block10];

        // IAFV: (even, even) positions.
        Span<float> coeff = stackalloc float[16];
        coeff[0] = dcs[0];
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                if (ix == 0 && iy == 0)
                {
                    continue;
                }

                coeff[(iy * 4) + ix] = coefficients[(iy * 2 * 8) + (ix * 2)];
            }
        }

        Span<float> block = stackalloc float[4 * 8];
        AfvIdct4x4(coeff, block);
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                pixels[((iy + (afvY * 4)) * stride) + (afvX * 4) + ix] =
                    block[((afvY == 1 ? 3 - iy : iy) * 4) + (afvX == 1 ? 3 - ix : ix)];
            }
        }

        // IDCT4x4 in (odd, even) positions.
        block[0] = dcs[1];
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                if (ix == 0 && iy == 0)
                {
                    continue;
                }

                block[(iy * 4) + ix] = coefficients[(iy * 2 * 8) + (ix * 2) + 1];
            }
        }

        ScaledIdct(4, 4, block[..16], pixels[((afvY * 4 * stride) + (afvX == 1 ? 0 : 4))..], stride, scratch);

        // IDCT4x8.
        block[0] = dcs[2];
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 8; ix++)
            {
                if (ix == 0 && iy == 0)
                {
                    continue;
                }

                block[(iy * 8) + ix] = coefficients[((1 + (iy * 2)) * 8) + ix];
            }
        }

        ScaledIdct(4, 8, block, pixels[((afvY == 1 ? 0 : 4) * stride)..], stride, scratch);
    }
}
