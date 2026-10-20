using System.Buffers;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Shared.Ccitt;

/// <summary>Thrown by the shared CCITT decoder for malformed data; format-level decoders translate it into their own exception type.</summary>
internal sealed class CcittDataException : Exception
{
    public CcittDataException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown by the shared CCITT decoder for well-formed data using a feature it does not implement (uncompressed mode).</summary>
internal sealed class CcittUnsupportedException : Exception
{
    public CcittUnsupportedException(string message)
        : base(message)
    {
    }
}

/// <summary>How a CCITT stream is laid out. Mirrors the PDF CCITTFaxDecode parameters, which also cover TIFF compressions 2, 3 and 4.</summary>
internal readonly record struct CcittParameters
{
    /// <summary>Negative: Group 4 (T.6, two-dimensional). Zero: Group 3 one-dimensional. Positive: Group 3 two-dimensional (an EOL + tag bit precedes each row).</summary>
    public int K { get; init; }

    /// <summary>Row width in pixels.</summary>
    public required int Columns { get; init; }

    /// <summary>Whether EOL codes are expected to be present in a Group 3 stream (they are tolerated, and consumed, either way).</summary>
    public bool EndOfLine { get; init; }

    /// <summary>Whether each encoded row starts on a byte boundary.</summary>
    public bool EncodedByteAlign { get; init; }

    /// <summary>Whether the stream ends with an end-of-block pattern (EOFB for Group 4, RTC for Group 3) which is consumed when found.</summary>
    public bool EndOfBlock { get; init; }

    /// <summary>How many damaged rows to patch over (repeating the previous row) before giving up. Recovery needs EOL codes to resynchronize on.</summary>
    public int DamagedRowsBeforeError { get; init; }

    /// <summary>Whether bits are packed least-significant-bit first within each byte (TIFF FillOrder 2).</summary>
    public bool LsbFirst { get; init; }
}

/// <summary>The packed output of <see cref="CcittDecoder.Decode"/>.</summary>
/// <param name="Rows">Number of rows decoded.</param>
/// <param name="Stride">Bytes per packed row.</param>
/// <param name="Bits">Packed rows, most significant bit first; a 1 bit is a black pixel.</param>
/// <param name="BytesConsumed">Bytes of the input the decoder used, rounded up to a whole byte.</param>
internal readonly record struct CcittResult(int Rows, int Stride, byte[] Bits, int BytesConsumed);

/// <summary>
/// ITU-T T.4 (Group 3, 1-D and 2-D) and T.6 (Group 4) fax decoder, producing packed one-bit rows with black pixels
/// set. Shared by TIFF (compressions 2/3/4), the PDF-style raw fax options and JBIG2's MMR coding.
/// </summary>
internal static class CcittDecoder
{
    private const int MaxPixelCount = 268_435_456;
    private const int MaxColumns = 1 << 24;

    private enum Mode
    {
        Pass,
        Horizontal,
        V0,
        VR1,
        VR2,
        VR3,
        VL1,
        VL2,
        VL3,
    }

    /// <summary>Decodes <paramref name="data"/> into packed rows. When <paramref name="rows"/> is zero the row count is whatever the data holds.</summary>
    public static CcittResult Decode(ReadOnlySpan<byte> data, in CcittParameters parameters, int rows)
    {
        int columns = parameters.Columns;
        if (columns <= 0 || columns > MaxColumns)
        {
            throw new CcittDataException($"CCITT row width {columns} is out of range.");
        }

        if (rows < 0 || (rows > 0 && (long)rows * columns > MaxPixelCount))
        {
            throw new CcittDataException($"CCITT row count {rows} is out of range.");
        }

        byte[]? reversed = null;
        if (parameters.LsbFirst)
        {
            reversed = ArrayPool<byte>.Shared.Rent(data.Length);
            ReverseBits(data, reversed);
            data = reversed.AsSpan(0, data.Length);
        }

        try
        {
            return DecodeCore(data, parameters, rows);
        }
        finally
        {
            if (reversed is not null)
            {
                ArrayPool<byte>.Shared.Return(reversed);
            }
        }
    }

    private static CcittResult DecodeCore(ReadOnlySpan<byte> data, in CcittParameters parameters, int rows)
    {
        int columns = parameters.Columns;

        int stride = (columns + 7) / 8;
        byte[] output = new byte[rows > 0 ? stride * rows : stride * 64];
        using var state = new State(data, columns);
        int k = parameters.K;
        int decoded = 0;
        int damaged = 0;

        // Consume an optional leading EOL (and tag bit) so the first row is aligned like every later one.
        bool next2D = k < 0;
        bool endReached = false;
        if (k >= 0)
        {
            bool sawEol = state.SkipFillAndEol();
            if (sawEol && state.IsEol())
            {
                // A stream that is nothing but an end-of-block.
                endReached = true;
            }
            else if (k > 0)
            {
                next2D = state.ReadTagBit() == 0;
            }
        }

        while ((rows == 0 || decoded < rows) && !endReached)
        {
            if (state.AtEnd || state.OnlyPaddingLeft())
            {
                break;
            }

            if (k < 0 && state.IsEol())
            {
                // EOFB.
                endReached = true;
                break;
            }

            bool ok = true;
            try
            {
                if (next2D)
                {
                    state.Decode2DRow();
                }
                else
                {
                    state.Decode1DRow();
                }
            }
            catch (CcittDataException) when (damaged < parameters.DamagedRowsBeforeError && k >= 0 && state.ResynchronizeAtEol())
            {
                ok = false;
                damaged++;
            }

            if (decoded * stride >= output.Length)
            {
                Array.Resize(ref output, output.Length * 2);
            }

            if (ok)
            {
                state.WriteRow(output.AsSpan(decoded * stride, stride));
                state.CommitRow();
            }
            else
            {
                // Patch the damaged row with a copy of the previous one (or white for the first).
                if (decoded > 0)
                {
                    output.AsSpan((decoded - 1) * stride, stride).CopyTo(output.AsSpan(decoded * stride, stride));
                }
            }

            decoded++;

            if (parameters.EncodedByteAlign)
            {
                state.AlignToByte();
            }

            if (rows > 0 && decoded == rows && !parameters.EndOfBlock)
            {
                break;
            }

            // Between rows: fill bits and an EOL (which Group 3 streams carry and Group 4 streams do not),
            // then for mixed-mode streams the tag bit that says how the next row is coded.
            if (k >= 0)
            {
                bool sawEol = state.SkipFillAndEol();
                if (sawEol && state.IsEol())
                {
                    endReached = true; // RTC: consecutive EOLs.
                }
                else if (k > 0 && !state.AtEnd)
                {
                    next2D = state.ReadTagBit() == 0;
                }
            }
        }

        if (parameters.EndOfBlock || k < 0)
        {
            // Swallow a trailing end-of-block (EOFB: two EOLs; RTC: six) so BytesConsumed covers it.
            state.ConsumeTrailingEol(k);
        }

        state.AlignToByte();
        if (rows > 0 && decoded < rows)
        {
            throw new CcittDataException($"CCITT data ended after {decoded} of {rows} rows.");
        }

        int consumed = Math.Min(state.BitPosition / 8, data.Length);
        var packed = rows > 0 && output.Length == stride * rows ? output : output.AsSpan(0, stride * decoded).ToArray();
        return new CcittResult(decoded, stride, packed, consumed);
    }

    private static readonly byte[] NibbleReverse = [0x0, 0x8, 0x4, 0xC, 0x2, 0xA, 0x6, 0xE, 0x1, 0x9, 0x5, 0xD, 0x3, 0xB, 0x7, 0xF];

    private static void ReverseBits(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated && source.Length >= Vector128<byte>.Count)
        {
            var lut = Vector128.Create(NibbleReverse);
            var lowMask = Vector128.Create((byte)0x0F);
            for (; i <= source.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                var v = Vector128.Create(source.Slice(i));
                var low = Vector128.Shuffle(lut, v & lowMask);
                var high = Vector128.Shuffle(lut, Vector128.ShiftRightLogical(v, 4));
                (Vector128.ShiftLeft(low, 4) | high).CopyTo(destination.Slice(i));
            }
        }

        for (; i < source.Length; i++)
        {
            byte b = source[i];
            destination[i] = (byte)((NibbleReverse[b & 0xF] << 4) | NibbleReverse[b >> 4]);
        }
    }

    private ref struct State
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int _columns;
        private readonly int _bitLength;
        private int _bitPos;
        private readonly int[] _ref;
        private readonly int[] _cod;
        private readonly int _lineLength;
        private int _a0i;
        private int _b1i;
        private int _black;

        public State(ReadOnlySpan<byte> data, int columns)
        {
            _data = data;
            _columns = columns;
            _bitLength = data.Length * 8;
            _bitPos = 0;
            _lineLength = columns + 4;
            _ref = ArrayPool<int>.Shared.Rent(_lineLength);
            _cod = ArrayPool<int>.Shared.Rent(_lineLength);
            _ref.AsSpan(0, _lineLength).Fill(columns);
            _cod.AsSpan(0, _lineLength).Fill(columns);
            _ref[0] = columns; // The reference line before the first row is all white.
            _a0i = 0;
            _b1i = 0;
            _black = 0;
        }

        public readonly void Dispose()
        {
            ArrayPool<int>.Shared.Return(_ref);
            ArrayPool<int>.Shared.Return(_cod);
        }

        public readonly int BitPosition => _bitPos;

        public readonly bool AtEnd => _bitPos >= _bitLength;

        private readonly int Peek(int count)
        {
            // count <= 24.
            int bytePos = _bitPos >> 3;
            uint window = 0;
            for (int i = 0; i < 4; i++)
            {
                window <<= 8;
                if (bytePos + i < _data.Length)
                {
                    window |= _data[bytePos + i];
                }
            }

            return (int)((window << (_bitPos & 7)) >> (32 - count));
        }

        private void Skip(int count)
        {
            _bitPos += count;
        }

        public void AlignToByte() => _bitPos = (_bitPos + 7) & ~7;

        /// <summary>Whether all that is left of the input is zero bits short of a whole byte (padding after the final row).</summary>
        public readonly bool OnlyPaddingLeft()
        {
            int left = _bitLength - _bitPos;
            return left > 0 && left < 8 && Peek(left) == 0;
        }

        public int ReadTagBit()
        {
            int bit = Peek(1);
            Skip(1);
            return bit;
        }

        /// <summary>Whether the next 12 bits are an EOL code (000000000001).</summary>
        public readonly bool IsEol() => !AtEnd && Peek(12) == 1;

        /// <summary>Skips zero fill bits and, when present, an EOL code. Returns whether an EOL was consumed.</summary>
        public bool SkipFillAndEol()
        {
            // Fill bits are zeros ahead of the EOL's own 11 zeros; a valid code never starts with 12 zero bits.
            while (!AtEnd && Peek(12) == 0)
            {
                Skip(1);
            }

            if (!AtEnd && Peek(12) == 1)
            {
                Skip(12);
                return true;
            }

            return false;
        }

        /// <summary>After a damaged row, scans forward for the next EOL and positions the reader after it. Returns false when none exists.</summary>
        public bool ResynchronizeAtEol()
        {
            while (_bitPos + 12 <= _bitLength)
            {
                if (Peek(12) == 1)
                {
                    return true;
                }

                _bitPos++;
            }

            return false;
        }

        public void ConsumeTrailingEol(int k)
        {
            int saved = _bitPos;
            int guard = 0;
            while (guard < 8 && SkipFillAndEol())
            {
                guard++;
                if (k > 0 && !AtEnd && Peek(1) == 1)
                {
                    Skip(1);
                }
            }

            if (guard == 0)
            {
                _bitPos = saved;
            }
        }

        private int ReadRun(int[] table)
        {
            int total = 0;
            while (true)
            {
                int entry = table[Peek(CcittCodeTables.LookupBits)];
                if (entry == 0)
                {
                    throw new CcittDataException("Invalid CCITT run-length code.");
                }

                Skip(entry >> 16);
                int run = entry & 0xFFFF;
                total += run;
                if (run < 64)
                {
                    return total;
                }

                if (total > _columns + 2560)
                {
                    throw new CcittDataException("CCITT run exceeds the row width.");
                }
            }
        }

        private void AddPixels(int a1, int black)
        {
            if (a1 > _cod[_a0i])
            {
                if (a1 > _columns)
                {
                    throw new CcittDataException("CCITT run extends past the end of the row.");
                }

                if (((_a0i & 1) ^ black) != 0)
                {
                    _a0i++;
                }

                _cod[_a0i] = a1;
            }
        }

        private void AddPixelsNeg(int a1, int black)
        {
            if (a1 > _cod[_a0i])
            {
                AddPixels(a1, black);
            }
            else if (a1 < _cod[_a0i])
            {
                if (a1 < 0)
                {
                    throw new CcittDataException("CCITT vertical code points before the start of the row.");
                }

                while (_a0i > 0 && a1 <= _cod[_a0i - 1])
                {
                    _a0i--;
                }

                _cod[_a0i] = a1;
            }
        }

        private void StartRow()
        {
            _cod.AsSpan(0, _lineLength).Fill(_columns);
            _cod[0] = 0;
            _a0i = 0;
            _b1i = 0;
            _black = 0;
        }

        public void Decode1DRow()
        {
            StartRow();
            while (_cod[_a0i] < _columns)
            {
                int run = ReadRun(_black != 0 ? CcittCodeTables.Black : CcittCodeTables.White);
                AddPixels(_cod[_a0i] + run, _black);
                _black ^= 1;
            }
        }

        private Mode ReadMode()
        {
            int bits = Peek(7);
            if ((bits & 0x40) != 0)
            {
                Skip(1);
                return Mode.V0;
            }

            if ((bits >> 4) == 0b011)
            {
                Skip(3);
                return Mode.VR1;
            }

            if ((bits >> 4) == 0b010)
            {
                Skip(3);
                return Mode.VL1;
            }

            if ((bits >> 4) == 0b001)
            {
                Skip(3);
                return Mode.Horizontal;
            }

            if ((bits >> 3) == 0b0001)
            {
                Skip(4);
                return Mode.Pass;
            }

            switch (bits >> 1)
            {
                case 0b000011:
                    Skip(6);
                    return Mode.VR2;
                case 0b000010:
                    Skip(6);
                    return Mode.VL2;
            }

            switch (bits)
            {
                case 0b0000011:
                    Skip(7);
                    return Mode.VR3;
                case 0b0000010:
                    Skip(7);
                    return Mode.VL3;
                case 0b0000001:
                    throw new CcittUnsupportedException("CCITT uncompressed mode is not supported.");
            }

            throw new CcittDataException("Invalid CCITT two-dimensional mode code.");
        }

        private void AdvanceB1()
        {
            while (_ref[_b1i] <= _cod[_a0i] && _ref[_b1i] < _columns)
            {
                _b1i += 2;
            }
        }

        public void Decode2DRow()
        {
            StartRow();
            while (_cod[_a0i] < _columns)
            {
                switch (ReadMode())
                {
                    case Mode.Pass:
                        AddPixels(_ref[_b1i + 1], _black);
                        if (_ref[_b1i + 1] < _columns)
                        {
                            _b1i += 2;
                        }

                        break;

                    case Mode.Horizontal:
                        int first = ReadRun(_black != 0 ? CcittCodeTables.Black : CcittCodeTables.White);
                        int second = ReadRun(_black != 0 ? CcittCodeTables.White : CcittCodeTables.Black);
                        AddPixels(_cod[_a0i] + first, _black);
                        if (_cod[_a0i] < _columns)
                        {
                            AddPixels(_cod[_a0i] + second, _black ^ 1);
                        }

                        AdvanceB1();
                        break;

                    case Mode.V0:
                        Vertical(0);
                        break;
                    case Mode.VR1:
                        Vertical(1);
                        break;
                    case Mode.VR2:
                        Vertical(2);
                        break;
                    case Mode.VR3:
                        Vertical(3);
                        break;
                    case Mode.VL1:
                        Vertical(-1);
                        break;
                    case Mode.VL2:
                        Vertical(-2);
                        break;
                    case Mode.VL3:
                        Vertical(-3);
                        break;
                }
            }
        }

        private void Vertical(int delta)
        {
            if (delta >= 0)
            {
                AddPixels(_ref[_b1i] + delta, _black);
            }
            else
            {
                AddPixelsNeg(_ref[_b1i] + delta, _black);
            }

            _black ^= 1;
            if (_cod[_a0i] < _columns)
            {
                if (delta >= 0)
                {
                    _b1i++;
                }
                else if (_b1i > 0)
                {
                    _b1i--;
                }
                else
                {
                    _b1i++;
                }

                AdvanceB1();
            }
        }

        /// <summary>Renders the row just decoded into <paramref name="row"/> (black runs become 1 bits).</summary>
        public readonly void WriteRow(Span<byte> row)
        {
            row.Clear();
            for (int i = 1; i <= _a0i && _cod[i - 1] < _columns; i += 2)
            {
                SetBits(row, _cod[i - 1], Math.Min(_cod[i], _columns));
            }
        }

        private static void SetBits(Span<byte> row, int start, int end)
        {
            if (start >= end)
            {
                return;
            }

            int firstByte = start >> 3;
            int lastByte = (end - 1) >> 3;
            byte head = (byte)(0xFF >> (start & 7));
            byte tail = (byte)(0xFF << (7 - ((end - 1) & 7)));
            if (firstByte == lastByte)
            {
                row[firstByte] |= (byte)(head & tail);
                return;
            }

            row[firstByte] |= head;
            row.Slice(firstByte + 1, lastByte - firstByte - 1).Fill(0xFF);
            row[lastByte] |= tail;
        }

        /// <summary>Makes the row just decoded the reference line for the next one.</summary>
        public void CommitRow()
        {
            int i = 0;
            for (; i < _lineLength && _cod[i] < _columns; i++)
            {
                _ref[i] = _cod[i];
            }

            _ref.AsSpan(i, _lineLength - i).Fill(_columns);
        }
    }
}
