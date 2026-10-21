using PeachImage.Formats.Shared.Arithmetic;

namespace PeachImage.Formats.Jpeg2000.Decoding;

/// <summary>One codeword segment of a code-block: its bytes and how many coding passes it holds.</summary>
internal readonly struct J2kSegmentData
{
    public J2kSegmentData(ReadOnlyMemory<byte> data, int passes)
    {
        Data = data;
        Passes = passes;
    }

    public ReadOnlyMemory<byte> Data { get; }

    public int Passes { get; }
}

/// <summary>Reusable buffers for <see cref="J2kBlockDecoder"/>; one per worker thread keeps block decoding allocation-free.</summary>
internal sealed class J2kBlockScratch
{
    public ushort[] Flags = [];
    public int[] Data = [];
    public readonly byte[] Contexts = new byte[19];
    public J2kSegmentData[] SegmentBuffer = new J2kSegmentData[8];
    public byte[][] Rented = new byte[8][];
}

/// <summary>Tier-1 (EBCOT) code-block decoding, T.800 Annex D.</summary>
internal ref struct J2kBlockDecoder
{
    public const int StyleBypass = 0x01;
    public const int StyleReset = 0x02;
    public const int StyleTermAll = 0x04;
    public const int StyleVerticalCausal = 0x08;
    public const int StyleSegmentationSymbols = 0x20;

    private const int SigNe = 0x0001;
    private const int SigSe = 0x0002;
    private const int SigSw = 0x0004;
    private const int SigNw = 0x0008;
    private const int SigN = 0x0010;
    private const int SigE = 0x0020;
    private const int SigS = 0x0040;
    private const int SigW = 0x0080;
    private const int SgnN = 0x0100;
    private const int SgnE = 0x0200;
    private const int SgnS = 0x0400;
    private const int SgnW = 0x0800;
    private const int Sig = 0x1000;
    private const int Refine = 0x2000;
    private const int Visit = 0x4000;
    private const int NeighborMask = 0x00FF;

    private const int ContextRunLength = 17;
    private const int ContextUniform = 18;
    private const int ContextCount = 19;

    // Zero-coding context labels indexed [orientation][neighbour significance byte].
    private static readonly byte[][] ZeroCodingTable = BuildZeroCodingTable();

    // Sign-coding: [(flags >> 4) & 0xFF] -> context label (low bits) and XOR bit (bit 7).
    private static readonly byte[] SignCodingTable = BuildSignCodingTable();

    private readonly J2kBlockScratch _scratch;
    private ushort[] _flags;
    private int[] _data;
    private readonly byte[] _contexts;
    private int _width;
    private int _height;
    private int _stride;
    private bool _verticalCausal;

    private MqDecoder _mq;
    private RawBitReader _raw;
    private bool _useRaw;

    public J2kBlockDecoder(J2kBlockScratch scratch)
    {
        _scratch = scratch;
        _flags = scratch.Flags;
        _data = scratch.Data;
        _contexts = scratch.Contexts;
        _width = 0;
        _height = 0;
        _stride = 0;
        _verticalCausal = false;
        _mq = default;
        _raw = default;
        _useRaw = false;
    }

    /// <summary>The decoded samples (sign-magnitude folded into a signed value with one fractional bit), row-major <c>width * height</c>.</summary>
    public ReadOnlySpan<int> Data => _data.AsSpan(0, _width * _height);

    /// <summary>
    /// Decodes a code-block. <paramref name="bitPlanes"/> is the number of coded magnitude bit-planes.
    /// After the call <see cref="Data"/> holds each sample as (value * 2), signed, so truncation by two gives the integer value.
    /// </summary>
    public void Decode(int width, int height, int orientation, int style, int bitPlanes, ReadOnlySpan<J2kSegmentData> segments)
    {
        _width = width;
        _height = height;
        _stride = width + 2;
        int flagCount = (_width + 2) * (_height + 2);
        if (_flags.Length < flagCount)
        {
            _flags = _scratch.Flags = new ushort[flagCount];
        }
        else
        {
            Array.Clear(_flags, 0, flagCount);
        }

        if (_data.Length < width * height)
        {
            _data = _scratch.Data = new int[width * height];
        }
        else
        {
            Array.Clear(_data, 0, width * height);
        }

        _verticalCausal = (style & StyleVerticalCausal) != 0;
        ResetContexts();

        var zc = ZeroCodingTable[orientation];
        int passNumber = 0;
        foreach (var segment in segments)
        {
            if (segment.Passes == 0)
            {
                continue;
            }

            int firstType = (passNumber + 2) % 3;
            _useRaw = (style & StyleBypass) != 0 && passNumber >= 10 && firstType < 2;
            if (_useRaw)
            {
                _raw = new RawBitReader(segment.Data.Span);
            }
            else
            {
                _mq = new MqDecoder(segment.Data.Span);
            }

            for (int p = 0; p < segment.Passes; p++, passNumber++)
            {
                int type = (passNumber + 2) % 3;
                int plane = bitPlanes - 1 - ((passNumber + 2) / 3);
                if (passNumber == 0)
                {
                    plane = bitPlanes - 1;
                }

                if (plane < 0)
                {
                    return;
                }

                int one = 1 << (plane + 1);
                int half = one >> 1;
                int oneAndHalf = one | half;
                switch (type)
                {
                    case 0:
                        SignificancePass(zc, oneAndHalf);
                        break;
                    case 1:
                        RefinementPass(half);
                        break;
                    default:
                        CleanupPass(zc, oneAndHalf);
                        if ((style & StyleSegmentationSymbols) != 0)
                        {
                            for (int s = 0; s < 4; s++)
                            {
                                _mq.Decode(ref _contexts[ContextUniform]);
                            }
                        }

                        break;
                }

                if ((style & StyleReset) != 0)
                {
                    ResetContexts();
                }
            }
        }
    }

    private void ResetContexts()
    {
        Array.Clear(_contexts);
        _contexts[0] = MqDecoder.MakeContext(4);
        _contexts[ContextRunLength] = MqDecoder.MakeContext(3);
        _contexts[ContextUniform] = MqDecoder.MakeContext(46);
    }

    private int DecodeBit(int context) => _mq.Decode(ref _contexts[context]);

    private void SetSignificant(int index, int negative)
    {
        var flags = _flags;
        int stride = _stride;
        flags[index] |= Sig;
        if (!(_verticalCausal && IsFirstRowOfStripe(index)))
        {
            flags[index - stride - 1] |= SigSe;
            flags[index - stride] |= (ushort)(SigS | (negative != 0 ? SgnS : 0));
            flags[index - stride + 1] |= SigSw;
        }

        flags[index - 1] |= (ushort)(SigE | (negative != 0 ? SgnE : 0));
        flags[index + 1] |= (ushort)(SigW | (negative != 0 ? SgnW : 0));
        flags[index + stride - 1] |= SigNe;
        flags[index + stride] |= (ushort)(SigN | (negative != 0 ? SgnN : 0));
        flags[index + stride + 1] |= SigNw;
    }

    private bool IsFirstRowOfStripe(int index)
    {
        int row = (index / _stride) - 1;
        return (row & 3) == 0;
    }

    private int DecodeSign(int flags)
    {
        byte entry = SignCodingTable[(flags >> 4) & 0xFF];
        int bit = DecodeBit(entry & 0x1F);
        return bit ^ (entry >> 7);
    }

    private void SignificancePass(byte[] zc, int oneAndHalf)
    {
        for (int k = 0; k < _height; k += 4)
        {
            for (int i = 0; i < _width; i++)
            {
                int rows = Math.Min(4, _height - k);
                for (int j = 0; j < rows; j++)
                {
                    int index = ((k + j + 1) * _stride) + i + 1;
                    int flags = _flags[index];
                    if ((flags & (Sig | Visit)) != 0 || (flags & NeighborMask) == 0)
                    {
                        continue;
                    }

                    int bit;
                    if (_useRaw)
                    {
                        bit = _raw.ReadBit();
                    }
                    else
                    {
                        bit = DecodeBit(zc[flags & NeighborMask]);
                    }

                    if (bit != 0)
                    {
                        int negative = _useRaw ? _raw.ReadBit() : DecodeSign(flags);
                        _data[((k + j) * _width) + i] = negative != 0 ? -oneAndHalf : oneAndHalf;
                        SetSignificant(index, negative);
                    }

                    _flags[index] |= Visit;
                }
            }
        }
    }

    private void RefinementPass(int half)
    {
        for (int k = 0; k < _height; k += 4)
        {
            for (int i = 0; i < _width; i++)
            {
                int rows = Math.Min(4, _height - k);
                for (int j = 0; j < rows; j++)
                {
                    int index = ((k + j + 1) * _stride) + i + 1;
                    int flags = _flags[index];
                    if ((flags & (Sig | Visit)) != Sig)
                    {
                        continue;
                    }

                    int bit;
                    if (_useRaw)
                    {
                        bit = _raw.ReadBit();
                    }
                    else
                    {
                        int context = (flags & Refine) != 0 ? 16 : ((flags & NeighborMask) != 0 ? 15 : 14);
                        bit = DecodeBit(context);
                    }

                    int delta = bit != 0 ? half : -half;
                    int position = ((k + j) * _width) + i;
                    _data[position] += _data[position] < 0 ? -delta : delta;
                    _flags[index] |= Refine;
                }
            }
        }
    }

    private void CleanupPass(byte[] zc, int oneAndHalf)
    {
        for (int k = 0; k < _height; k += 4)
        {
            for (int i = 0; i < _width; i++)
            {
                int rows = Math.Min(4, _height - k);
                int start = 0;
                int index0 = ((k + 1) * _stride) + i + 1;
                if (rows == 4
                    && ((_flags[index0] | _flags[index0 + _stride] | _flags[index0 + (2 * _stride)] | _flags[index0 + (3 * _stride)]) & (Sig | Visit | NeighborMask)) == 0)
                {
                    if (DecodeBit(ContextRunLength) == 0)
                    {
                        continue;
                    }

                    int run = DecodeBit(ContextUniform) << 1;
                    run |= DecodeBit(ContextUniform);
                    int index = index0 + (run * _stride);
                    int negative = DecodeSign(_flags[index]);
                    _data[((k + run) * _width) + i] = negative != 0 ? -oneAndHalf : oneAndHalf;
                    SetSignificant(index, negative);
                    start = run + 1;
                }

                for (int j = start; j < rows; j++)
                {
                    int index = index0 + (j * _stride);
                    int flags = _flags[index];
                    if ((flags & (Sig | Visit)) == 0)
                    {
                        if (DecodeBit(zc[flags & NeighborMask]) != 0)
                        {
                            int negative = DecodeSign(flags);
                            _data[((k + j) * _width) + i] = negative != 0 ? -oneAndHalf : oneAndHalf;
                            SetSignificant(index, negative);
                        }
                    }
                }

                for (int j = 0; j < rows; j++)
                {
                    _flags[index0 + (j * _stride)] &= unchecked((ushort)~Visit);
                }
            }
        }
    }

    private static byte[][] BuildZeroCodingTable()
    {
        var tables = new byte[4][];
        for (int orientation = 0; orientation < 4; orientation++)
        {
            var table = new byte[256];
            for (int f = 0; f < 256; f++)
            {
                int h = ((f & SigW) != 0 ? 1 : 0) + ((f & SigE) != 0 ? 1 : 0);
                int v = ((f & SigN) != 0 ? 1 : 0) + ((f & SigS) != 0 ? 1 : 0);
                int d = ((f & SigNe) != 0 ? 1 : 0) + ((f & SigNw) != 0 ? 1 : 0) + ((f & SigSe) != 0 ? 1 : 0) + ((f & SigSw) != 0 ? 1 : 0);
                if (orientation == 1)
                {
                    (h, v) = (v, h);
                }

                int label;
                if (orientation == 3)
                {
                    int hv = h + v;
                    label = d >= 3 ? 8
                        : d == 2 ? (hv >= 1 ? 7 : 6)
                        : d == 1 ? (hv >= 2 ? 5 : hv == 1 ? 4 : 3)
                        : (hv >= 2 ? 2 : hv == 1 ? 1 : 0);
                }
                else
                {
                    label = h == 2 ? 8
                        : h == 1 ? (v >= 1 ? 7 : d >= 1 ? 6 : 5)
                        : v == 2 ? 4
                        : v == 1 ? 3
                        : d >= 2 ? 2
                        : d == 1 ? 1
                        : 0;
                }

                table[f] = (byte)label;
            }

            tables[orientation] = table;
        }

        return tables;
    }

    private static byte[] BuildSignCodingTable()
    {
        var table = new byte[256];
        for (int index = 0; index < 256; index++)
        {
            int Contribution(int sigBit, int signBit) => (index & sigBit) == 0 ? 0 : ((index & signBit) != 0 ? -1 : 1);

            // index bits: 0 N, 1 E, 2 S, 3 W significant; 4 N, 5 E, 6 S, 7 W negative.
            int h = Math.Clamp(Contribution(0x02, 0x20) + Contribution(0x08, 0x80), -1, 1);
            int v = Math.Clamp(Contribution(0x01, 0x10) + Contribution(0x04, 0x40), -1, 1);
            int context;
            int xor = 0;
            if (h == 1)
            {
                context = v == 1 ? 13 : v == 0 ? 12 : 11;
            }
            else if (h == 0)
            {
                context = v == 0 ? 9 : 10;
                xor = v == -1 ? 1 : 0;
            }
            else
            {
                context = v == 1 ? 11 : v == 0 ? 12 : 13;
                xor = 1;
            }

            table[index] = (byte)(context | (xor << 7));
        }

        return table;
    }

    /// <summary>Raw (bypass) bit reader with the 0xFF bit-stuffing rule: the byte after 0xFF carries seven bits.</summary>
    private ref struct RawBitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;
        private int _current;
        private int _count;

        public RawBitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
            _current = 0;
            _count = 0;
        }

        public int ReadBit()
        {
            if (_count == 0)
            {
                int previous = _current;
                _current = _position < _data.Length ? _data[_position++] : 0xFF;
                _count = previous == 0xFF ? 7 : 8;
            }

            _count--;
            return (_current >> _count) & 1;
        }
    }
}
