namespace PeachImage.Formats.Shared.Arithmetic;

/// <summary>
/// The MQ arithmetic decoder shared by JBIG2 (ITU-T T.88 Annex E) and JPEG 2000 (T.800 Annex C), in the software
/// convention. Probability contexts live in caller-owned byte arrays: each byte is <c>(stateIndex &lt;&lt; 1) | mps</c>.
/// </summary>
internal ref struct MqDecoder
{
    private static readonly ushort[] Qe =
    [
        0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401,
        0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201,
        0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1,
        0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
    ];

    private static readonly byte[] NextMps =
    [
        1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
        29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
    ];

    private static readonly byte[] NextLps =
    [
        1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
    ];

    private static readonly bool[] Switch =
    [
        true, false, false, false, false, false, true, false, false, false, false, false, false, false, true,
        false, false, false, false, false, false, false, false, false, false, false, false, false, false, false,
        false, false, false, false, false, false, false, false, false, false, false, false, false, false, false,
        false, false,
    ];

    // Context byte -> context byte after decoding an MPS / an LPS (the switch already applied).
    private static readonly byte[] MpsTransition = BuildTransitions(lps: false);
    private static readonly byte[] LpsTransition = BuildTransitions(lps: true);

    private readonly ReadOnlySpan<byte> _data;
    private int _bp;
    private uint _c;
    private uint _a;
    private int _ct;

    /// <summary>Starts decoding at the beginning of <paramref name="data"/>.</summary>
    public MqDecoder(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bp = 0;
        _ct = 0;
        _a = 0;
        _c = (uint)Get(0) << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    /// <summary>The number of bytes the decoder has read so far (an upper bound on what the coded data used).</summary>
    public readonly int Position => _bp;

    /// <summary>The initial context byte: state 0, MPS 0.</summary>
    public const byte InitialContext = 0;

    /// <summary>Sets the initial state for a JPEG 2000 context: <paramref name="stateIndex"/> with MPS 0.</summary>
    public static byte MakeContext(int stateIndex) => (byte)(stateIndex << 1);

    private readonly byte Get(int index) => (uint)index < (uint)_data.Length ? _data[index] : (byte)0xFF;

    private void ByteIn()
    {
        if (Get(_bp) == 0xFF)
        {
            if (Get(_bp + 1) > 0x8F)
            {
                _c += 0xFF00;
                _ct = 8;
            }
            else
            {
                _bp++;
                _c += (uint)Get(_bp) << 9;
                _ct = 7;
            }
        }
        else
        {
            _bp++;
            _c += (uint)Get(_bp) << 8;
            _ct = 8;
        }
    }

    /// <summary>Decodes one binary decision using (and updating) <paramref name="context"/>.</summary>
    public int Decode(ref byte context)
    {
        byte cx = context;
        int index = cx >> 1;
        int mps = cx & 1;
        uint qe = Qe[index];
        _a -= qe;
        int d;
        if ((_c >> 16) < qe)
        {
            if (_a < qe)
            {
                d = mps;
                context = MpsTransition[cx];
            }
            else
            {
                d = 1 - mps;
                context = LpsTransition[cx];
            }

            _a = qe;
            Renormalize();
        }
        else
        {
            _c -= qe << 16;
            if ((_a & 0x8000) == 0)
            {
                if (_a < qe)
                {
                    d = 1 - mps;
                    context = LpsTransition[cx];
                }
                else
                {
                    d = mps;
                    context = MpsTransition[cx];
                }

                Renormalize();
            }
            else
            {
                d = mps;
            }
        }

        return d;
    }

    private void Renormalize()
    {
        do
        {
            if (_ct == 0)
            {
                ByteIn();
            }

            _a <<= 1;
            _c <<= 1;
            _ct--;
        }
        while ((_a & 0x8000) == 0);
    }

    private static byte[] BuildTransitions(bool lps)
    {
        var table = new byte[94];
        for (int index = 0; index < 47; index++)
        {
            for (int mps = 0; mps < 2; mps++)
            {
                int next = lps ? NextLps[index] : NextMps[index];
                int nextMps = lps && Switch[index] ? 1 - mps : mps;
                table[(index << 1) | mps] = (byte)((next << 1) | nextMps);
            }
        }

        return table;
    }
}
