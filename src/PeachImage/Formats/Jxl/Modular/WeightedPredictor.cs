using System.Numerics;
using PeachImage.Formats.Jxl.Bitstream;

namespace PeachImage.Formats.Jxl.Modular;

/// <summary>Tuning parameters of the self-correcting weighted predictor, optionally overridden in a Modular stream's header.</summary>
internal sealed class WeightedPredictorHeader
{
    public static WeightedPredictorHeader Default { get; } = new();

    public int P1C { get; private set; } = 16;

    public int P2C { get; private set; } = 10;

    public int P3Ca { get; private set; } = 7;

    public int P3Cb { get; private set; } = 7;

    public int P3Cc { get; private set; } = 7;

    public int P3Cd { get; private set; }

    public int P3Ce { get; private set; }

    public uint[] Weights { get; private set; } = [0xd, 0xc, 0xc, 0xc];

    public static WeightedPredictorHeader Read(ref JxlBitReader br)
    {
        if (br.ReadBool())
        {
            return Default;
        }

        var header = new WeightedPredictorHeader
        {
            P1C = (int)br.ReadBits(5),
            P2C = (int)br.ReadBits(5),
            P3Ca = (int)br.ReadBits(5),
            P3Cb = (int)br.ReadBits(5),
            P3Cc = (int)br.ReadBits(5),
            P3Cd = (int)br.ReadBits(5),
            P3Ce = (int)br.ReadBits(5),
            Weights = [br.ReadBits(4), br.ReadBits(4), br.ReadBits(4), br.ReadBits(4)],
        };
        br.ThrowIfOverrun();
        return header;
    }
}

/// <summary>
/// The weighted ("self-correcting") predictor state for one channel: it blends four sub-predictors, weighting each by
/// how well it predicted recent neighbours, and also yields a property (the largest recent error) for the MA tree.
/// </summary>
internal sealed class WeightedPredictorState
{
    private const int NumPredictors = 4;
    private const int PredExtraBits = 3;
    private const long PredictionRound = ((1 << PredExtraBits) >> 1) - 1;

    // divlookup[i] = (1 << 24) / (i + 1), approximating division by 1..64.
    private static readonly uint[] DivLookup =
    [
        16777216, 8388608, 5592405, 4194304, 3355443, 2796202, 2396745, 2097152,
        1864135, 1677721, 1525201, 1398101, 1290555, 1198372, 1118481, 1048576,
        986895, 932067, 883011, 838860, 798915, 762600, 729444, 699050,
        671088, 645277, 621378, 599186, 578524, 559240, 541200, 524288,
        508400, 493447, 479349, 466033, 453438, 441505, 430185, 419430,
        409200, 399457, 390167, 381300, 372827, 364722, 356962, 349525,
        342392, 335544, 328965, 322638, 316551, 310689, 305040, 299593,
        294337, 289262, 284359, 279620, 275036, 270600, 266305, 262144,
    ];

    private readonly WeightedPredictorHeader _header;
    private readonly uint[][] _predictionErrors;
    private readonly int[] _error;
    private readonly long[] _prediction = new long[NumPredictors];
    private long _pred;

    public WeightedPredictorState(WeightedPredictorHeader header, int width)
    {
        _header = header;
        _predictionErrors = new uint[NumPredictors][];
        for (int i = 0; i < NumPredictors; i++)
        {
            _predictionErrors[i] = new uint[(width + 2) * 2];
        }

        _error = new int[(width + 2) * 2];
    }

    private static long AddBits(long x) => (long)((ulong)x << PredExtraBits);

    private static uint ErrorWeight(ulong x, uint maxWeight)
    {
        int shift = BitOperations.Log2(x + 1) - 5;
        if (shift < 0)
        {
            shift = 0;
        }

        return 4 + ((maxWeight * DivLookup[x >> shift]) >> shift);
    }

    private long WeightedAverage(Span<uint> weights)
    {
        uint weightSum = 0;
        for (int i = 0; i < NumPredictors; i++)
        {
            weightSum += weights[i];
        }

        int logWeight = BitOperations.Log2(weightSum);
        weightSum = 0;
        for (int i = 0; i < NumPredictors; i++)
        {
            weights[i] >>= logWeight - 4;
            weightSum += weights[i];
        }

        long sum = (weightSum >> 1) - 1;
        for (int i = 0; i < NumPredictors; i++)
        {
            sum += _prediction[i] * weights[i];
        }

        return (sum * DivLookup[weightSum - 1]) >> 24;
    }

    /// <summary>
    /// Predicts the pixel at (<paramref name="x"/>, <paramref name="y"/>) from its already-decoded neighbours, and
    /// returns the "max error" property for the MA tree in <paramref name="property"/>.
    /// </summary>
    public long Predict(int x, int y, int width, long n, long w, long ne, long nw, long nn, out int property)
    {
        int curRow = (y & 1) != 0 ? 0 : width + 2;
        int prevRow = (y & 1) != 0 ? width + 2 : 0;
        int posN = prevRow + x;
        int posNe = x < width - 1 ? posN + 1 : posN;
        int posNw = x > 0 ? posN - 1 : posN;

        Span<uint> weights = stackalloc uint[NumPredictors];
        for (int i = 0; i < NumPredictors; i++)
        {
            var errors = _predictionErrors[i];
            uint sum = errors[posN] + errors[posNe] + errors[posNw];
            weights[i] = ErrorWeight(sum, _header.Weights[i]);
        }

        n = AddBits(n);
        w = AddBits(w);
        ne = AddBits(ne);
        nw = AddBits(nw);
        nn = AddBits(nn);

        long teW = x == 0 ? 0 : _error[curRow + x - 1];
        long teN = _error[posN];
        long teNw = _error[posNw];
        long sumWn = teN + teW;
        long teNe = _error[posNe];

        long p = teW;
        if (Math.Abs(teN) > Math.Abs(p))
        {
            p = teN;
        }

        if (Math.Abs(teNw) > Math.Abs(p))
        {
            p = teNw;
        }

        if (Math.Abs(teNe) > Math.Abs(p))
        {
            p = teNe;
        }

        property = (int)p;

        _prediction[0] = w + ne - n;
        _prediction[1] = n - (((sumWn + teNe) * _header.P1C) >> 5);
        _prediction[2] = w - (((sumWn + teNw) * _header.P2C) >> 5);
        _prediction[3] = n - (((teNw * _header.P3Ca) + (teN * _header.P3Cb) + (teNe * _header.P3Cc)
            + ((nn - n) * _header.P3Cd) + ((nw - w) * _header.P3Ce)) >> 5);

        _pred = WeightedAverage(weights);

        // If the three errors share a sign, skip clamping.
        if (((teN ^ teW) | (teN ^ teNw)) > 0)
        {
            return (_pred + PredictionRound) >> PredExtraBits;
        }

        // Otherwise clamp to the range of the neighbouring pixels (W, NE, N).
        long max = Math.Max(w, Math.Max(ne, n));
        long min = Math.Min(w, Math.Min(ne, n));
        _pred = Math.Max(min, Math.Min(max, _pred));
        return (_pred + PredictionRound) >> PredExtraBits;
    }

    /// <summary>Records the true value of the pixel just predicted so later predictions can weight themselves by recent accuracy.</summary>
    public void UpdateErrors(long value, int x, int y, int width)
    {
        int curRow = (y & 1) != 0 ? 0 : width + 2;
        int prevRow = (y & 1) != 0 ? width + 2 : 0;
        value = AddBits(value);
        _error[curRow + x] = (int)(_pred - value);
        for (int i = 0; i < NumPredictors; i++)
        {
            uint err = (uint)((Math.Abs(_prediction[i] - value) + PredictionRound) >> PredExtraBits);
            _predictionErrors[i][curRow + x] = err;

            // Add this pixel's error to its NE neighbour, which stands in for the E and EE pixels.
            _predictionErrors[i][prevRow + x + 1] += err;
        }
    }
}
