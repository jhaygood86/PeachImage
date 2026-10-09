using PeachImage.Formats.Jxl.Bitstream;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>Distance-band parameters of a DCT-style quantization table (up to 17 bands per channel).</summary>
internal sealed class DctQuantWeightParams
{
    public const int Log2MaxDistanceBands = 4;
    public const int MaxDistanceBands = 1 + (1 << Log2MaxDistanceBands);

    public DctQuantWeightParams(int numBands, float[][] bands)
    {
        NumDistanceBands = numBands;
        DistanceBands = bands;
    }

    public int NumDistanceBands { get; }

    /// <summary>Per channel, the band values.</summary>
    public float[][] DistanceBands { get; }
}

internal enum QuantMode
{
    Library = 0,
    Identity = 1,
    Dct2 = 2,
    Dct4 = 3,
    Dct4x8 = 4,
    Afv = 5,
    Dct = 6,
    Raw = 7,
}

/// <summary>How one quantization table is specified: a library default, or parameters of one of the table families.</summary>
internal sealed class QuantEncoding
{
    public QuantMode Mode { get; set; } = QuantMode.Library;

    public DctQuantWeightParams? DctParams { get; set; }

    public DctQuantWeightParams? DctParamsAfv4x4 { get; set; }

    public float[][]? IdWeights { get; set; }

    public float[][]? Dct2Weights { get; set; }

    public float[][]? Dct4Multipliers { get; set; }

    public float[]? Dct4x8Multipliers { get; set; }

    public float[][]? AfvWeights { get; set; }

    /// <summary>Raw tables: the explicit quantization values (3 x X x Y) and their denominator.</summary>
    public int[]? RawTable { get; set; }

    public float RawDenominator { get; set; } = 1f / (8 * 255);
}

/// <summary>Reads RAW quantization tables, which are stored as small Modular images.</summary>
internal interface IQuantTableReader
{
    /// <summary>Decodes a <paramref name="width"/> x <paramref name="height"/> 3-channel table with quant-table index <paramref name="index"/>.</summary>
    int[] ReadRawTable(ref JxlBitReader br, int width, int height, int index);
}

/// <summary>
/// The 17 dequantization matrices of a VarDCT frame: decoded from the frame (or defaulted from a built-in library), then
/// expanded into per-coefficient dequantization multipliers for each channel.
/// </summary>
internal sealed class DequantMatrices
{
    public const int NumQuantTables = 17;


    private const float AlmostZero = 1e-8f;
    private const float Sqrt2 = 1.41421356237f;

    // Required table size in 8x8 blocks, for each table.
    private static readonly int[] RequiredSizeX = [1, 1, 1, 1, 2, 4, 1, 1, 2, 1, 1, 8, 4, 16, 8, 32, 16];
    private static readonly int[] RequiredSizeY = [1, 1, 1, 1, 2, 4, 2, 4, 4, 1, 1, 8, 8, 16, 16, 32, 32];

    // Which table each AC strategy uses.
    private static readonly byte[] StrategyToTable = [0, 1, 2, 3, 4, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 10, 10, 11, 12, 12, 13, 14, 14, 15, 16, 16];

    private static readonly float[] DefaultDcQuant = [1.0f / 4096.0f, 1.0f / 512.0f, 1.0f / 256.0f];

    private static readonly QuantEncoding[] Library = BuildLibrary();

    private readonly QuantEncoding[] _encodings = new QuantEncoding[NumQuantTables];
    private readonly float[]?[] _tables = new float[]?[NumQuantTables];

    public DequantMatrices()
    {
        for (int i = 0; i < _encodings.Length; i++)
        {
            _encodings[i] = new QuantEncoding();
        }
    }

    /// <summary>The DC dequantization multipliers for X, Y, B.</summary>
    /// <summary>The 17 quantization table encodings.</summary>
    public IReadOnlyList<QuantEncoding> Encodings => _encodings;

    public float[] DcQuant { get; private set; } = (float[])DefaultDcQuant.Clone();

    /// <summary>Reads the DC multipliers (the first part of the frame's global section).</summary>
    public void ReadDc(ref JxlBitReader br)
    {
        bool allDefault = br.ReadBool();
        br.ThrowIfOverrun();
        if (allDefault)
        {
            return;
        }

        var dc = new float[3];
        for (int c = 0; c < 3; c++)
        {
            dc[c] = JxlFieldReader.ReadF16(ref br) * (1.0f / 128.0f);
            if (dc[c] < AlmostZero)
            {
                throw new JxlDecodingException("Invalid DC quantization factor.");
            }
        }

        br.ThrowIfOverrun();
        DcQuant = dc;
    }

    /// <summary>Reads the AC quantization table encodings.</summary>
    public void Read(ref JxlBitReader br, IQuantTableReader rawReader)
    {
        bool allDefault = br.ReadBool();
        for (int i = 0; i < NumQuantTables; i++)
        {
            _encodings[i] = allDefault ? new QuantEncoding() : ReadEncoding(ref br, i, rawReader);
            _tables[i] = null;
        }

        br.ThrowIfOverrun();
    }

    /// <summary>
    /// Returns the dequantization multipliers for AC strategy <paramref name="strategy"/>: three consecutive channel planes of
    /// <c>covered blocks * 64</c> floats in the strategy's coefficient layout.
    /// </summary>
    public float[] Matrix(int strategy)
    {
        int table = StrategyToTable[strategy];
        return _tables[table] ??= ComputeTable(table);
    }

    private static DctQuantWeightParams ReadDctParams(ref JxlBitReader br)
    {
        int numBands = (int)br.ReadBits(DctQuantWeightParams.Log2MaxDistanceBands) + 1;
        var bands = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            bands[c] = new float[numBands];
            for (int i = 0; i < numBands; i++)
            {
                bands[c][i] = JxlFieldReader.ReadF16(ref br);
            }

            if (bands[c][0] < AlmostZero)
            {
                throw new JxlDecodingException("Distance band seed is too small.");
            }

            bands[c][0] *= 64.0f;
        }

        return new DctQuantWeightParams(numBands, bands);
    }

    private static float[][] ReadMatrix(ref JxlBitReader br, int rows, int cols, float scale, bool requireNonZero, string what)
    {
        var result = new float[rows][];
        for (int r = 0; r < rows; r++)
        {
            result[r] = new float[cols];
            for (int c = 0; c < cols; c++)
            {
                result[r][c] = JxlFieldReader.ReadF16(ref br);
                if (requireNonZero && Math.Abs(result[r][c]) < AlmostZero)
                {
                    throw new JxlDecodingException(what);
                }

                result[r][c] *= scale;
            }
        }

        return result;
    }

    private static QuantEncoding ReadEncoding(ref JxlBitReader br, int index, IQuantTableReader rawReader)
    {
        int requiredSize = RequiredSizeX[index] * RequiredSizeY[index];
        var mode = (QuantMode)br.ReadBits(3);
        var encoding = new QuantEncoding { Mode = mode };
        switch (mode)
        {
            case QuantMode.Library:
                // Only the single predefined table exists; its index takes zero bits.
                break;
            case QuantMode.Identity:
                RequireOneBlock(requiredSize);
                encoding.IdWeights = ReadMatrix(ref br, 3, 3, 64f, true, "ID quantizer is too small.");
                break;
            case QuantMode.Dct2:
                RequireOneBlock(requiredSize);
                encoding.Dct2Weights = ReadMatrix(ref br, 3, 6, 64f, true, "Quantizer is too small.");
                break;
            case QuantMode.Dct4x8:
                RequireOneBlock(requiredSize);
                encoding.Dct4x8Multipliers = ReadMatrix(ref br, 1, 3, 1f, true, "DCT4X8 multiplier is too small.")[0];
                encoding.DctParams = ReadDctParams(ref br);
                break;
            case QuantMode.Dct4:
                RequireOneBlock(requiredSize);
                encoding.Dct4Multipliers = ReadMatrix(ref br, 3, 2, 1f, true, "DCT4 multiplier is too small.");
                encoding.DctParams = ReadDctParams(ref br);
                break;
            case QuantMode.Afv:
                RequireOneBlock(requiredSize);
                encoding.AfvWeights = new float[3][];
                for (int c = 0; c < 3; c++)
                {
                    encoding.AfvWeights[c] = new float[9];
                    for (int i = 0; i < 9; i++)
                    {
                        encoding.AfvWeights[c][i] = JxlFieldReader.ReadF16(ref br);
                    }

                    for (int i = 0; i < 6; i++)
                    {
                        encoding.AfvWeights[c][i] *= 64f;
                    }
                }

                encoding.DctParams = ReadDctParams(ref br);
                encoding.DctParamsAfv4x4 = ReadDctParams(ref br);
                break;
            case QuantMode.Dct:
                encoding.DctParams = ReadDctParams(ref br);
                break;
            case QuantMode.Raw:
                encoding.RawDenominator = JxlFieldReader.ReadF16(ref br);
                if (encoding.RawDenominator < AlmostZero)
                {
                    throw new JxlDecodingException("Invalid raw quantization denominator.");
                }

                encoding.RawTable = rawReader.ReadRawTable(ref br, RequiredSizeX[index] * 8, RequiredSizeY[index] * 8, index);
                break;
        }

        br.ThrowIfOverrun();
        return encoding;
    }

    private static void RequireOneBlock(int requiredSize)
    {
        if (requiredSize != 1)
        {
            throw new JxlDecodingException("Invalid quantization table mode for this table size.");
        }
    }

    private float[] ComputeTable(int table)
    {
        var encoding = _encodings[table].Mode == QuantMode.Library ? Library[table] : _encodings[table];
        int rows = 8 * RequiredSizeX[table];
        int cols = 8 * RequiredSizeY[table];
        int num = rows * cols;
        var weights = new float[3 * num];

        switch (encoding.Mode)
        {
            case QuantMode.Identity:
                WeightsIdentity(encoding.IdWeights!, weights);
                break;
            case QuantMode.Dct2:
                WeightsDct2(encoding.Dct2Weights!, weights);
                break;
            case QuantMode.Dct4:
            {
                var weights4x4 = new float[3 * 16];
                GetQuantWeights(4, 4, encoding.DctParams!, weights4x4);
                for (int c = 0; c < 3; c++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            weights[(c * num) + (y * 8) + x] = weights4x4[(c * 16) + ((y / 2) * 4) + (x / 2)];
                        }
                    }

                    weights[(c * num) + 1] /= encoding.Dct4Multipliers![c][0];
                    weights[(c * num) + 8] /= encoding.Dct4Multipliers[c][0];
                    weights[(c * num) + 9] /= encoding.Dct4Multipliers[c][1];
                }

                break;
            }

            case QuantMode.Dct4x8:
            {
                var weights4x8 = new float[3 * 32];
                GetQuantWeights(4, 8, encoding.DctParams!, weights4x8);
                for (int c = 0; c < 3; c++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            weights[(c * num) + (y * 8) + x] = weights4x8[(c * 32) + ((y / 2) * 8) + x];
                        }
                    }

                    weights[(c * num) + 8] /= encoding.Dct4x8Multipliers![c];
                }

                break;
            }

            case QuantMode.Dct:
                GetQuantWeights(rows, cols, encoding.DctParams!, weights);
                break;
            case QuantMode.Raw:
            {
                int[] raw = encoding.RawTable!;
                if (raw.Length != 3 * num)
                {
                    throw new JxlDecodingException("Invalid raw quantization table encoding.");
                }

                for (int i = 0; i < weights.Length; i++)
                {
                    weights[i] = 1f / (encoding.RawDenominator * raw[i]);
                }

                break;
            }

            case QuantMode.Afv:
                WeightsAfv(encoding, weights, num);
                break;
            default:
                throw new JxlDecodingException("Invalid quantization table encoding.");
        }

        var result = new float[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            float inv = weights[i];
            if (inv >= 1.0f / AlmostZero || inv < AlmostZero)
            {
                throw new JxlDecodingException("Invalid quantization table.");
            }

            result[i] = 1.0f / inv;
        }

        return result;
    }

    private static void WeightsDct2(float[][] w, float[] weights)
    {
        for (int c = 0; c < 3; c++)
        {
            int start = c * 64;
            weights[start] = 0xBAD;
            weights[start + 1] = weights[start + 8] = w[c][0];
            weights[start + 9] = w[c][1];
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    weights[start + (y * 8) + x + 2] = w[c][2];
                    weights[start + ((y + 2) * 8) + x] = w[c][2];
                    weights[start + ((y + 2) * 8) + x + 2] = w[c][3];
                }
            }

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    weights[start + (y * 8) + x + 4] = w[c][4];
                    weights[start + ((y + 4) * 8) + x] = w[c][4];
                    weights[start + ((y + 4) * 8) + x + 4] = w[c][5];
                }
            }
        }
    }

    private static void WeightsIdentity(float[][] w, float[] weights)
    {
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < 64; i++)
            {
                weights[(64 * c) + i] = w[c][0];
            }

            weights[(64 * c) + 1] = w[c][1];
            weights[(64 * c) + 8] = w[c][1];
            weights[(64 * c) + 9] = w[c][2];
        }
    }

    private static void WeightsAfv(QuantEncoding encoding, float[] weights, int num)
    {
        ReadOnlySpan<float> freqs =
        [
            0xBAD, 0xBAD, 0.8517778890324296f, 5.37778436506804f,
            0xBAD, 0xBAD, 4.734747904497923f, 5.449245381693219f,
            1.6598270267479331f, 4f, 7.275749096817861f, 10.423227632456525f,
            2.662932286148962f, 7.630657783650829f, 8.962388608184032f, 12.97166202570235f,
        ];

        var weights4x8 = new float[3 * 32];
        GetQuantWeights(4, 8, encoding.DctParams!, weights4x8);
        var weights4x4 = new float[3 * 16];
        GetQuantWeights(4, 4, encoding.DctParamsAfv4x4!, weights4x4);

        const float lo = 0.8517778890324296f;
        const float hi = 12.97166202570235f - lo + 1e-6f;
        var afv = encoding.AfvWeights!;
        for (int c = 0; c < 3; c++)
        {
            var bands = new float[4];
            bands[0] = afv[c][5];
            if (bands[0] < AlmostZero)
            {
                throw new JxlDecodingException("Invalid AFV bands.");
            }

            for (int i = 1; i < 4; i++)
            {
                bands[i] = bands[i - 1] * Mult(afv[c][i + 5]);
                if (bands[i] < AlmostZero)
                {
                    throw new JxlDecodingException("Invalid AFV bands.");
                }
            }

            int start = c * 64;
            weights[start] = 1;

            // Weights for (0, 1) and (1, 0), and the AFV 3-pixel corner.
            weights[start + (1 * 8) + 0] = afv[c][0];
            weights[start + (0 * 8) + 1] = afv[c][1];
            weights[start + (2 * 8) + 0] = afv[c][2];
            weights[start + (0 * 8) + 2] = afv[c][3];
            weights[start + (2 * 8) + 2] = afv[c][4];

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    if (x < 2 && y < 2)
                    {
                        continue;
                    }

                    float val = Interpolate(freqs[(y * 4) + x] - lo, hi, bands);
                    weights[start + (2 * y * 8) + (2 * x)] = val;
                }
            }

            // 4x8 weights in odd rows, except (1, 0).
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    if (x == 0 && y == 0)
                    {
                        continue;
                    }

                    weights[(c * num) + (((2 * y) + 1) * 8) + x] = weights4x8[(c * 32) + (y * 8) + x];
                }
            }

            // 4x4 weights in even rows / odd columns, except (0, 1).
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    if (x == 0 && y == 0)
                    {
                        continue;
                    }

                    weights[(c * num) + (2 * y * 8) + (2 * x) + 1] = weights4x4[(c * 16) + (y * 4) + x];
                }
            }
        }
    }

    private static float Mult(float v) => v > 0.0f ? 1.0f + v : 1.0f / (1.0f - v);

    // Geometric interpolation of `array` at scaled position pos/max * (len - 1).
    private static float Interpolate(float pos, float max, float[] array)
    {
        float scaledPos = pos * (array.Length - 1) / max;
        int idx = (int)scaledPos;
        if (idx + 1 >= array.Length)
        {
            throw new JxlDecodingException("Invalid AFV interpolation.");
        }

        float a = array[idx];
        float b = array[idx + 1];
        return a * MathF.Pow(b / a, scaledPos - idx);
    }

    // Weights for a ROWS x COLS block from per-channel distance bands, geometrically interpolated by eccentricity.
    private static void GetQuantWeights(int rows, int cols, DctQuantWeightParams p, float[] output)
    {
        int numBands = p.NumDistanceBands;
        for (int c = 0; c < 3; c++)
        {
            var bands = new float[DctQuantWeightParams.MaxDistanceBands];
            bands[0] = p.DistanceBands[c][0];
            if (bands[0] < AlmostZero)
            {
                throw new JxlDecodingException("Invalid distance bands.");
            }

            for (int i = 1; i < numBands; i++)
            {
                bands[i] = bands[i - 1] * Mult(p.DistanceBands[c][i]);
                if (bands[i] < AlmostZero)
                {
                    throw new JxlDecodingException("Invalid distance bands.");
                }
            }

            float scale = (numBands - 1) / (Sqrt2 + 1e-6f);
            float rcpcol = scale / (cols - 1);
            float rcprow = scale / (rows - 1);
            for (int y = 0; y < rows; y++)
            {
                float dy = y * rcprow;
                float dy2 = dy * dy;
                for (int x = 0; x < cols; x++)
                {
                    float dx = x * rcpcol;
                    float distance = MathF.Sqrt((dx * dx) + dy2);
                    float weight;
                    if (numBands == 1)
                    {
                        weight = bands[0];
                    }
                    else
                    {
                        int idx = Math.Min((int)distance, numBands - 2);
                        float frac = distance - idx;
                        float a = bands[idx];
                        float b = bands[idx + 1];
                        weight = a * MathF.Pow(b / a, frac);
                    }

                    output[(c * cols * rows) + (y * cols) + x] = weight;
                }
            }
        }
    }

    private static QuantEncoding[] BuildLibrary()
    {
        static DctQuantWeightParams P(int bands, float[] x, float[] y, float[] b) => new(bands, [x, y, b]);

        var library = new QuantEncoding[NumQuantTables];

        // DCT8.
        library[0] = Dct(P(6, [3150.0f, 0.0f, -0.4f, -0.4f, -0.4f, -2.0f], [560.0f, 0.0f, -0.3f, -0.3f, -0.3f, -0.3f], [512.0f, -2.0f, -1.0f, 0.0f, -1.0f, -2.0f]));

        library[1] = new QuantEncoding
        {
            Mode = QuantMode.Identity,
            IdWeights = [[280.0f, 3160.0f, 3160.0f], [60.0f, 864.0f, 864.0f], [18.0f, 200.0f, 200.0f]],
        };

        library[2] = new QuantEncoding
        {
            Mode = QuantMode.Dct2,
            Dct2Weights = [[3840.0f, 2560.0f, 1280.0f, 640.0f, 480.0f, 300.0f], [960.0f, 640.0f, 320.0f, 180.0f, 140.0f, 120.0f], [640.0f, 320.0f, 128.0f, 64.0f, 32.0f, 16.0f]],
        };

        var dct4x4 = P(4, [2200.0f, 0.0f, 0.0f, 0.0f], [392.0f, 0.0f, 0.0f, 0.0f], [112.0f, -0.25f, -0.25f, -0.5f]);
        library[3] = new QuantEncoding
        {
            Mode = QuantMode.Dct4,
            DctParams = dct4x4,
            Dct4Multipliers = [[1.0f, 1.0f], [1.0f, 1.0f], [1.0f, 1.0f]],
        };

        library[4] = Dct(P(7, [8996.8725711814115328f, -1.3000777393353804f, -0.49424529824571225f, -0.439093774457103443f, -0.6350101832695744f, -0.90177264050827612f, -1.6162099239887414f], [3191.48366296844234752f, -0.67424582104194355f, -0.80745813428471001f, -0.44925837484843441f, -0.35865440981033403f, -0.31322389111877305f, -0.37615025315725483f], [1157.50408145487200256f, -2.0531423165804414f, -1.4f, -0.50687130033378396f, -0.42708730624733904f, -1.4856834539296244f, -4.9209142884401604f]));
        library[5] = Dct(P(8, [15718.40830982518931456f, -1.025f, -0.98f, -0.9012f, -0.4f, -0.48819395464f, -0.421064f, -0.27f], [7305.7636810695983104f, -0.8041958212306401f, -0.7633036457487539f, -0.55660379990111464f, -0.49785304658857626f, -0.43699592683512467f, -0.40180866526242109f, -0.27321683125358037f], [3803.53173721215041536f, -3.060733579805728f, -2.0413270132490346f, -2.0235650159727417f, -0.5495389509954993f, -0.4f, -0.4f, -0.3f]));
        library[6] = Dct(P(7, [7240.7734393502f, -0.7f, -0.7f, -0.2f, -0.2f, -0.2f, -0.5f], [1448.15468787004f, -0.5f, -0.5f, -0.5f, -0.2f, -0.2f, -0.2f], [506.854140754517f, -1.4f, -0.2f, -0.5f, -0.5f, -1.5f, -3.6f]));
        library[7] = Dct(P(8, [16283.2494710648897f, -1.7812845336559429f, -1.6309059012653515f, -1.0382179034313539f, -0.85f, -0.7f, -0.9f, -1.2360638576849587f], [5089.15750884921511936f, -0.320049391452786891f, -0.35362849922161446f, -0.30340000000000003f, -0.61f, -0.5f, -0.5f, -0.6f], [3397.77603275308720128f, -0.321327362693153371f, -0.34507619223117997f, -0.70340000000000003f, -0.9f, -1.0f, -1.0f, -1.1754605576265209f]));
        library[8] = Dct(P(8, [13844.97076442300573f, -0.97113799999999995f, -0.658f, -0.42026f, -0.22712f, -0.2206f, -0.226f, -0.6f], [4798.964084220744293f, -0.61125308982767057f, -0.83770786552491361f, -0.79014862079498627f, -0.2692727459704829f, -0.38272769465388551f, -0.22924222653091453f, -0.20719098826199578f], [1807.236946760964614f, -1.2f, -1.2f, -0.7f, -0.7f, -0.7f, -0.4f, -0.5f]));

        var dct4x8 = P(4, [2198.050556016380522f, -0.96269623020744692f, -0.76194253026666783f, -0.6551140670773547f], [764.3655248643528689f, -0.92630200888366945f, -0.9675229603596517f, -0.27845290869168118f], [527.107573587542228f, -1.4594385811273854f, -1.450082094097871593f, -1.5843722511996204f]);
        library[9] = new QuantEncoding { Mode = QuantMode.Dct4x8, DctParams = dct4x8, Dct4x8Multipliers = [1.0f, 1.0f, 1.0f] };

        library[10] = new QuantEncoding
        {
            Mode = QuantMode.Afv,
            DctParams = dct4x8,
            DctParamsAfv4x4 = dct4x4,
            AfvWeights =
            [
                [3072.0f, 3072.0f, 256.0f, 256.0f, 256.0f, 414.0f, 0.0f, 0.0f, 0.0f],
                [1024.0f, 1024.0f, 50f, 50f, 50f, 58.0f, 0.0f, 0.0f, 0.0f],
                [384.0f, 384.0f, 12.0f, 12.0f, 12.0f, 22.0f, -0.25f, -0.25f, -0.25f],
            ],
        };

        // Large transforms share one shape; only the leading scale differs.
        static DctQuantWeightParams Large(double sx, double sy, double sb, double xBase, double yBase, double bBase) => new(8,
        [
            [(float)(sx * xBase), -1.025f, -0.78f, -0.65012f, -0.19041574084286472f, -0.20819395464f, -0.421064f, -0.32733845535848671f],
            [(float)(sy * yBase), -0.3041958212306401f, -0.3633036457487539f, -0.35660379990111464f, -0.3443074455424403f, -0.33699592683512467f, -0.30180866526242109f, -0.27321683125358037f],
            [(float)(sb * bBase), -1.2f, -1.2f, -0.8f, -0.7f, -0.7f, -0.4f, -0.5f],
        ]);

        library[11] = Dct(Large(0.9, 0.9, 0.9, 26629.073922049845, 9311.3238710010046, 4992.2486445538634));
        library[12] = Dct(Large(0.65, 0.65, 0.65, 23629.073922049845, 8611.3238710010046, 4492.2486445538634));
        library[13] = Dct(Large(1.8, 1.8, 1.8, 26629.073922049845, 9311.3238710010046, 4992.2486445538634));
        library[14] = Dct(Large(1.3, 1.3, 1.3, 23629.073922049845, 8611.3238710010046, 4492.2486445538634));
        library[15] = Dct(Large(3.6, 3.6, 3.6, 26629.073922049845, 9311.3238710010046, 4992.2486445538634));
        library[16] = Dct(Large(2.6, 2.6, 2.6, 23629.073922049845, 8611.3238710010046, 4492.2486445538634));
        return library;

        static QuantEncoding Dct(DctQuantWeightParams p) => new() { Mode = QuantMode.Dct, DctParams = p };
    }
}
