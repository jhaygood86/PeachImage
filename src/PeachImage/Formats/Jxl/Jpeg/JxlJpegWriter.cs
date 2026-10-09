using System.Numerics;

namespace PeachImage.Formats.Jxl.Jpeg;

/// <summary>
/// Serializes a <see cref="JxlJpegData"/> back into the exact bytes of the JPEG it was made from: markers in their original
/// order, Huffman-coded scans reproduced bit for bit (including restart intervals, padding bits, end-of-block runs and
/// progressive refinement passes).
/// </summary>
internal sealed class JxlJpegWriter : IDisposable
{
    private const int Precision = 8;

    private readonly JxlJpegData _jpeg;
    private readonly MemoryStream _output = new();
    private readonly HuffmanTable[] _dcTables = new HuffmanTable[JxlJpegData.MaxHuffmanTables];
    private readonly HuffmanTable[] _acTables = new HuffmanTable[JxlJpegData.MaxHuffmanTables];

    // Entropy-coded bit output.
    private ulong _bitBuffer;
    private int _bitCount;

    // Progressive coding state carried between blocks.
    private int _eobRun;
    private HuffmanTable? _eobTable;
    private readonly List<ushort> _refinementBits = [];
    private int _refinementBitCount;

    private int _dhtIndex;
    private int _dqtIndex;
    private int _appIndex;
    private int _comIndex;
    private int _dataIndex;
    private int _scanIndex;
    private bool _seenDri;
    private bool _progressive;
    private int _padPosition;

    private JxlJpegWriter(JxlJpegData jpeg)
    {
        _jpeg = jpeg;
        for (int i = 0; i < JxlJpegData.MaxHuffmanTables; i++)
        {
            _dcTables[i] = new HuffmanTable();
            _acTables[i] = new HuffmanTable();
        }
    }

    public void Dispose() => _output.Dispose();

    private sealed class HuffmanTable
    {
        public readonly sbyte[] Depth = new sbyte[256];
        public readonly ushort[] Code = new ushort[256];
        public bool Initialized;
    }

    /// <summary>Writes the JPEG file described by <paramref name="jpeg"/>.</summary>
    public static byte[] Write(JxlJpegData jpeg)
    {
        using var writer = new JxlJpegWriter(jpeg);
        writer.WriteAll();
        return writer._output.ToArray();
    }

    private static JxlDecodingException Fail(string what) => new($"JPEG reconstruction failed: {what}");

    private void WriteAll()
    {
        if (_jpeg.MarkerOrder.Count == 0)
        {
            throw Fail("no markers");
        }

        _output.WriteByte(0xFF);
        _output.WriteByte(0xD8);
        foreach (byte marker in _jpeg.MarkerOrder)
        {
            WriteSection(marker);
        }

        if (_jpeg.HasZeroPaddingBit && _padPosition != _jpeg.PaddingBits.Count)
        {
            throw Fail("invalid number of padding bits");
        }
    }

    private void WriteSection(byte marker)
    {
        switch (marker)
        {
            case 0xC0:
            case 0xC1:
            case 0xC2:
            case 0xC9:
            case 0xCA:
                WriteSof(marker);
                break;
            case 0xC4:
                WriteDht();
                break;
            case >= 0xD0 and <= 0xD7:
                _output.WriteByte(0xFF);
                _output.WriteByte(marker);
                break;
            case 0xD9:
                _output.WriteByte(0xFF);
                _output.WriteByte(0xD9);
                _output.Write(_jpeg.TailData);
                break;
            case 0xDA:
                WriteScan();
                break;
            case 0xDB:
                WriteDqt();
                break;
            case 0xDD:
                _seenDri = true;
                _output.Write([0xFF, 0xDD, 0, 4, (byte)(_jpeg.RestartInterval >> 8), (byte)(_jpeg.RestartInterval & 0xFF)]);
                break;
            case >= 0xE0 and <= 0xEF:
                if (_appIndex >= _jpeg.AppData.Count)
                {
                    throw Fail("missing application marker");
                }

                _output.WriteByte(0xFF);
                _output.Write(_jpeg.AppData[_appIndex++]);
                break;
            case 0xFE:
                if (_comIndex >= _jpeg.ComData.Count)
                {
                    throw Fail("missing comment marker");
                }

                _output.WriteByte(0xFF);
                _output.Write(_jpeg.ComData[_comIndex++]);
                break;
            case 0xFF:
                if (_dataIndex >= _jpeg.InterMarkerData.Count)
                {
                    throw Fail("missing inter-marker data");
                }

                _output.Write(_jpeg.InterMarkerData[_dataIndex++]);
                break;
            default:
                throw Fail($"unsupported marker 0x{marker:X2}");
        }
    }

    private void WriteSof(byte marker)
    {
        if (marker <= 0xC2)
        {
            _progressive = marker == 0xC2;
        }

        int count = _jpeg.Components.Count;
        int length = 8 + (3 * count);
        _output.Write([0xFF, marker, (byte)(length >> 8), (byte)(length & 0xFF), Precision, (byte)(_jpeg.Height >> 8), (byte)(_jpeg.Height & 0xFF), (byte)(_jpeg.Width >> 8), (byte)(_jpeg.Width & 0xFF), (byte)count]);
        foreach (var component in _jpeg.Components)
        {
            if (component.QuantTableIndex >= _jpeg.Quant.Count)
            {
                throw Fail("component refers to a missing quantization table");
            }

            _output.WriteByte((byte)component.Id);
            _output.WriteByte((byte)((component.HorizontalSamplingFactor << 4) | component.VerticalSamplingFactor));
            _output.WriteByte((byte)_jpeg.Quant[(int)component.QuantTableIndex].Index);
        }
    }

    private void WriteDht()
    {
        var codes = _jpeg.HuffmanCodes;
        int length = 2;
        for (int i = _dhtIndex; i < codes.Count; i++)
        {
            foreach (uint count in codes[i].Counts)
            {
                length += (int)count;
            }

            if (length == 2)
            {
                break;
            }

            length += JpegHuffmanCode.MaxBitLength;
            if (codes[i].IsLast)
            {
                break;
            }
        }

        _output.Write([0xFF, 0xC4, (byte)(length >> 8), (byte)(length & 0xFF)]);
        while (true)
        {
            if (_dhtIndex >= codes.Count)
            {
                throw Fail("missing Huffman code");
            }

            var huff = codes[_dhtIndex++];
            int index = huff.SlotId;
            int totalCount = 0;
            int maxLength = 0;
            for (int i = 0; i < huff.Counts.Length; i++)
            {
                if (huff.Counts[i] != 0)
                {
                    maxLength = i;
                }

                totalCount += (int)huff.Counts[i];
            }

            if (totalCount == 0)
            {
                break;
            }

            HuffmanTable table;
            if ((index & 0x10) != 0)
            {
                table = _acTables[index - 0x10];
            }
            else
            {
                table = _dcTables[index];
            }

            Array.Fill(table.Depth, (sbyte)127);
            BuildHuffmanTable(huff, table);
            table.Initialized = true;
            totalCount--;
            _output.WriteByte((byte)huff.SlotId);
            for (int i = 1; i <= JpegHuffmanCode.MaxBitLength; i++)
            {
                _output.WriteByte((byte)(i == maxLength ? huff.Counts[i] - 1 : huff.Counts[i]));
            }

            for (int i = 0; i < totalCount; i++)
            {
                _output.WriteByte((byte)huff.Values[i]);
            }

            if (huff.IsLast)
            {
                break;
            }
        }
    }

    private static void BuildHuffmanTable(JpegHuffmanCode huff, HuffmanTable table)
    {
        var huffCode = new int[JpegHuffmanCode.AlphabetSize + 1];
        var huffSize = new uint[JpegHuffmanCode.AlphabetSize + 2];
        int p = 0;
        for (int l = 1; l <= JpegHuffmanCode.MaxBitLength; l++)
        {
            int count = (int)huff.Counts[l];
            if (p + count > JpegHuffmanCode.AlphabetSize + 1)
            {
                throw Fail("invalid Huffman code");
            }

            while (count-- > 0)
            {
                huffSize[p++] = (uint)l;
            }
        }

        if (p == 0)
        {
            return;
        }

        // The last code is the virtual one; its slot becomes the sentinel.
        int lastP = p - 1;
        huffSize[lastP] = 0;
        int code = 0;
        uint si = huffSize[0];
        p = 0;
        while (huffSize[p] != 0)
        {
            while (huffSize[p] == si)
            {
                huffCode[p++] = code;
                code++;
            }

            code <<= 1;
            si++;
        }

        for (p = 0; p < lastP; p++)
        {
            int symbol = (int)huff.Values[p];
            table.Depth[symbol] = (sbyte)huffSize[p];
            table.Code[symbol] = (ushort)huffCode[p];
        }
    }

    private void WriteDqt()
    {
        int length = 2;
        for (int i = _dqtIndex; i < _jpeg.Quant.Count; i++)
        {
            var table = _jpeg.Quant[i];
            length += 1 + ((table.Precision != 0 ? 2 : 1) * JxlJpegData.DctBlockSize);
            if (table.IsLast)
            {
                break;
            }
        }

        _output.Write([0xFF, 0xDB, (byte)(length >> 8), (byte)(length & 0xFF)]);
        while (true)
        {
            if (_dqtIndex >= _jpeg.Quant.Count)
            {
                throw Fail("missing quantization table");
            }

            var table = _jpeg.Quant[_dqtIndex++];
            _output.WriteByte((byte)((table.Precision << 4) + table.Index));
            for (int i = 0; i < JxlJpegData.DctBlockSize; i++)
            {
                int value = table.Values[JxlJpegData.NaturalOrder[i]];
                if (table.Precision != 0)
                {
                    _output.WriteByte((byte)(value >> 8));
                }

                _output.WriteByte((byte)(value & 0xFF));
            }

            if (table.IsLast)
            {
                break;
            }
        }
    }

    // ---- Entropy-coded bits ----

    private void WriteBits(int count, ulong bits)
    {
        if (count > 56)
        {
            throw Fail("a symbol is missing from its Huffman table");
        }

        _bitBuffer = (_bitBuffer << count) | (bits & ((1UL << count) - 1));
        _bitCount += count;
        while (_bitCount >= 8)
        {
            EmitByte((int)((_bitBuffer >> (_bitCount - 8)) & 0xFF));
            _bitCount -= 8;
        }

        _bitBuffer &= (1UL << _bitCount) - 1;
    }

    private void EmitByte(int value)
    {
        _output.WriteByte((byte)value);
        if (value == 0xFF)
        {
            _output.WriteByte(0);
        }
    }

    private void WriteSymbol(int symbol, HuffmanTable table) => WriteBits(table.Depth[symbol], table.Code[symbol]);

    private void WriteSymbolBits(int symbol, HuffmanTable table, int count, ulong bits) =>
        WriteBits(count + table.Depth[symbol], bits | ((ulong)table.Code[symbol] << count));

    // Pads the current byte with the recorded padding bits (or ones when none were recorded) and ends the entropy-coded segment.
    private void JumpToByteBoundary()
    {
        int padCount = (8 - (_bitCount & 7)) & 7;
        if (padCount == 0)
        {
            return;
        }

        uint pattern;
        if (!_jpeg.HasZeroPaddingBit)
        {
            pattern = (1u << padCount) - 1;
        }
        else
        {
            pattern = 0;
            for (int i = 0; i < padCount; i++)
            {
                if (_padPosition >= _jpeg.PaddingBits.Count)
                {
                    throw Fail("not enough padding bits");
                }

                byte bit = _jpeg.PaddingBits[_padPosition++];
                if ((bit & ~1) != 0)
                {
                    throw Fail("invalid padding bit");
                }

                pattern = (pattern << 1) | bit;
            }
        }

        WriteBits(padCount, pattern);
    }

    // ---- Progressive state ----

    private void Flush()
    {
        if (_eobRun > 0)
        {
            int bits = 31 - BitOperations.LeadingZeroCount((uint)_eobRun);
            WriteSymbol(bits << 4, _eobTable!);
            if (bits > 0)
            {
                WriteBits(bits, (ulong)_eobRun & ((1UL << bits) - 1));
            }

            _eobRun = 0;
        }

        int words = _refinementBitCount >> 4;
        for (int i = 0; i < words; i++)
        {
            WriteBits(16, _refinementBits[i]);
        }

        int tail = _refinementBitCount & 0xF;
        if (tail != 0)
        {
            WriteBits(tail, _refinementBits[^1]);
        }

        _refinementBits.Clear();
        _refinementBitCount = 0;
    }

    private void BufferEndOfBand(HuffmanTable acTable, ReadOnlySpan<int> newBitsArray)
    {
        if (_eobRun == 0)
        {
            _eobTable = acTable;
        }

        _eobRun++;
        int newBitsCount = newBitsArray.Length;
        if (newBitsCount > 0)
        {
            ulong newBits = 0;
            foreach (int bit in newBitsArray)
            {
                newBits = (newBits << 1) | (uint)bit;
            }

            int tail = _refinementBitCount & 0xF;
            if (tail != 0)
            {
                int stuffCount = Math.Min(16 - tail, newBitsCount);
                ushort stuff = (ushort)((newBits >> (newBitsCount - stuffCount)) & ((1u << stuffCount) - 1));
                _refinementBits[^1] = (ushort)((_refinementBits[^1] << stuffCount) | stuff);
                newBitsCount -= stuffCount;
                _refinementBitCount += stuffCount;
            }

            while (newBitsCount >= 16)
            {
                _refinementBits.Add((ushort)(newBits >> (newBitsCount - 16)));
                newBitsCount -= 16;
                _refinementBitCount += 16;
            }

            if (newBitsCount > 0)
            {
                _refinementBits.Add((ushort)(newBits & ((1u << newBitsCount) - 1)));
                _refinementBitCount += newBitsCount;
            }
        }

        if (_eobRun == 0x7FFF)
        {
            Flush();
        }
    }

    // ---- Scans ----

    private void WriteScan()
    {
        var scan = _jpeg.Scans[_scanIndex];
        int length = 6 + (2 * (int)scan.NumComponents);
        _output.Write([0xFF, 0xDA, (byte)(length >> 8), (byte)(length & 0xFF), (byte)scan.NumComponents]);
        for (int i = 0; i < scan.NumComponents; i++)
        {
            var info = scan.Components[i];
            if (info.ComponentIndex >= _jpeg.Components.Count)
            {
                throw Fail("scan refers to a missing component");
            }

            _output.WriteByte((byte)_jpeg.Components[(int)info.ComponentIndex].Id);
            _output.WriteByte((byte)((info.DcTableIndex << 4) + info.AcTableIndex));
        }

        _output.WriteByte((byte)scan.Ss);
        _output.WriteByte((byte)scan.Se);
        _output.WriteByte((byte)((scan.Ah << 4) | scan.Al));

        _bitBuffer = 0;
        _bitCount = 0;
        _eobRun = 0;
        _refinementBits.Clear();
        _refinementBitCount = 0;

        int al = _progressive ? (int)scan.Al : 0;
        int ah = _progressive ? (int)scan.Ah : 0;
        int ss = _progressive ? (int)scan.Ss : 0;
        int se = _progressive ? (int)scan.Se : 63;
        bool sequential = !_progressive || (ah == 0 && al == 0 && ss == 0 && se == 63);
        int mode = sequential ? 0 : ah == 0 ? 1 : 2;
        EncodeScan(scan, mode, ss, se, al);
        _scanIndex++;
    }

    private void EncodeScan(JpegScanInfo scan, int mode, int ss, int se, int al)
    {
        int restartInterval = _seenDri ? (int)_jpeg.RestartInterval : 0;
        bool interleaved = scan.NumComponents > 1;
        _jpeg.CalculateMcuSize(scan, out int mcusPerRow, out int mcuRows);
        bool wantAc = ss != 0 || se != 0;
        bool wantDc = ss == 0;

        int restartsToGo = restartInterval;
        int nextRestartMarker = 0;
        int blockScanIndex = 0;
        int extraZeroRunsPos = 0;
        int nextExtraZeroRunIndex = scan.ExtraZeroRuns.Count > 0 ? (int)scan.ExtraZeroRuns[0].BlockIndex : -1;
        int resetPointPos = 0;
        int nextResetPoint = scan.ResetPoints.Count > 0 ? (int)scan.ResetPoints[resetPointPos++] : -1;
        var lastDc = new short[JxlJpegData.MaxComponents];

        for (int mcuY = 0; mcuY < mcuRows; mcuY++)
        {
            for (int mcuX = 0; mcuX < mcusPerRow; mcuX++)
            {
                if (restartInterval > 0 && restartsToGo == 0)
                {
                    Flush();
                    JumpToByteBoundary();
                    _output.WriteByte(0xFF);
                    _output.WriteByte((byte)(0xD0 + nextRestartMarker));
                    nextRestartMarker = (nextRestartMarker + 1) & 7;
                    restartsToGo = restartInterval;
                    Array.Clear(lastDc);
                }

                for (int i = 0; i < scan.NumComponents; i++)
                {
                    var info = scan.Components[i];
                    var component = _jpeg.Components[(int)info.ComponentIndex];
                    var dcTable = _dcTables[info.DcTableIndex];
                    var acTable = _acTables[info.AcTableIndex];
                    if (wantDc && !dcTable.Initialized)
                    {
                        throw Fail("DC Huffman table is not initialized");
                    }

                    if (wantAc && !acTable.Initialized)
                    {
                        throw Fail("AC Huffman table is not initialized");
                    }

                    int blocksY = interleaved ? component.VerticalSamplingFactor : 1;
                    int blocksX = interleaved ? component.HorizontalSamplingFactor : 1;
                    for (int iy = 0; iy < blocksY; iy++)
                    {
                        for (int ix = 0; ix < blocksX; ix++)
                        {
                            long blockY = ((long)mcuY * blocksY) + iy;
                            long blockX = ((long)mcuX * blocksX) + ix;
                            long blockIndex = (blockY * component.WidthInBlocks) + blockX;
                            if (blockScanIndex == nextResetPoint)
                            {
                                Flush();
                                nextResetPoint = resetPointPos < scan.ResetPoints.Count ? (int)scan.ResetPoints[resetPointPos++] : -1;
                            }

                            int zeroRuns = 0;
                            if (blockScanIndex == nextExtraZeroRunIndex)
                            {
                                zeroRuns = (int)scan.ExtraZeroRuns[extraZeroRunsPos].ExtraZeroRuns;
                                extraZeroRunsPos++;
                                nextExtraZeroRunIndex = extraZeroRunsPos < scan.ExtraZeroRuns.Count ? (int)scan.ExtraZeroRuns[extraZeroRunsPos].BlockIndex : -1;
                            }

                            long offset = blockIndex << 6;
                            if (offset < 0 || offset + JxlJpegData.DctBlockSize > component.Coefficients.Length)
                            {
                                throw Fail("block index out of range");
                            }

                            var coefficients = component.Coefficients.AsSpan((int)offset, JxlJpegData.DctBlockSize);
                            int componentIndex = (int)info.ComponentIndex;
                            if (mode == 0)
                            {
                                EncodeBlockSequential(coefficients, dcTable, acTable, zeroRuns, ref lastDc[componentIndex]);
                            }
                            else if (mode == 1)
                            {
                                EncodeBlockProgressive(coefficients, dcTable, acTable, ss, se, al, zeroRuns, ref lastDc[componentIndex]);
                            }
                            else
                            {
                                EncodeRefinement(coefficients, acTable, ss, se, al);
                            }

                            blockScanIndex++;
                        }
                    }
                }

                restartsToGo--;
            }
        }

        Flush();
        JumpToByteBoundary();
    }

    private void EncodeBlockSequential(ReadOnlySpan<short> coefficients, HuffmanTable dcTable, HuffmanTable acTable, int zeroRuns, ref short lastDc)
    {
        short dc = coefficients[0];
        short diff = (short)(dc - lastDc);
        lastDc = dc;
        int sign = diff >> 15;
        int adjusted = diff + sign;
        int magnitude = sign ^ adjusted;
        int dcBits = magnitude == 0 ? 0 : BitOperations.Log2((uint)magnitude) + 1;
        WriteSymbol(dcBits, dcTable);
        if (dcBits > 0)
        {
            WriteBits(dcBits, (ulong)adjusted & ((1UL << dcBits) - 1));
        }

        int run = 0;
        for (int i = 1; i < 64; i++)
        {
            int value = coefficients[(int)JxlJpegData.NaturalOrder[i]];
            if (value == 0)
            {
                run++;
                continue;
            }

            int s = value >> 31;
            value += s;
            int abs = s ^ value;
            for (int z = 0; z < 3 && run > 15; z++)
            {
                WriteSymbol(0xF0, acTable);
                run -= 16;
            }

            int acBits = BitOperations.Log2((uint)(ushort)abs) + 1;
            WriteSymbolBits((run << 4) + acBits, acTable, acBits, (ulong)value & ((1UL << acBits) - 1));
            run = 0;
        }

        for (int i = 0; i < zeroRuns; i++)
        {
            WriteSymbol(0xF0, acTable);
            run -= 16;
        }

        if (run > 0)
        {
            WriteSymbol(0, acTable);
        }
    }

    private void EncodeBlockProgressive(ReadOnlySpan<short> coefficients, HuffmanTable dcTable, HuffmanTable acTable, int ss, int se, int al, int zeroRuns, ref short lastDc)
    {
        bool eobRunAllowed = ss > 0;
        if (ss == 0)
        {
            int shifted = coefficients[0] >> al;
            short diff = (short)(shifted - lastDc);
            lastDc = (short)shifted;
            int value = diff;
            int bitsValue = value;
            if (value < 0)
            {
                value = -value;
                bitsValue--;
            }

            int bits = value == 0 ? 0 : BitOperations.Log2((uint)value) + 1;
            WriteSymbol(bits, dcTable);
            if (bits > 0)
            {
                WriteBits(bits, (ulong)bitsValue & ((1UL << bits) - 1));
            }

            ss++;
        }

        if (ss > se)
        {
            return;
        }

        int run = 0;
        for (int k = ss; k <= se; k++)
        {
            int value = coefficients[(int)JxlJpegData.NaturalOrder[k]];
            if (value == 0)
            {
                run++;
                continue;
            }

            int bitsValue;
            if (value < 0)
            {
                value = -value;
                value >>= al;
                bitsValue = ~value;
            }
            else
            {
                value >>= al;
                bitsValue = value;
            }

            if (value == 0)
            {
                run++;
                continue;
            }

            Flush();
            while (run > 15)
            {
                WriteSymbol(0xF0, acTable);
                run -= 16;
            }

            int bits = BitOperations.Log2((uint)value) + 1;
            WriteSymbol((run << 4) + bits, acTable);
            WriteBits(bits, (ulong)bitsValue & ((1UL << bits) - 1));
            run = 0;
        }

        if (zeroRuns > 0)
        {
            Flush();
            for (int i = 0; i < zeroRuns; i++)
            {
                WriteSymbol(0xF0, acTable);
                run -= 16;
            }
        }

        if (run > 0)
        {
            BufferEndOfBand(acTable, []);
            if (!eobRunAllowed)
            {
                Flush();
            }
        }
    }

    private void EncodeRefinement(ReadOnlySpan<short> coefficients, HuffmanTable acTable, int ss, int se, int al)
    {
        bool eobRunAllowed = ss > 0;
        if (ss == 0)
        {
            WriteBits(1, (ulong)((coefficients[0] >> al) & 1));
            ss++;
        }

        if (ss > se)
        {
            return;
        }

        Span<int> absValues = stackalloc int[JxlJpegData.DctBlockSize];
        int eob = 0;
        for (int k = ss; k <= se; k++)
        {
            int abs = Math.Abs((int)coefficients[(int)JxlJpegData.NaturalOrder[k]]);
            absValues[k] = abs >> al;
            if (absValues[k] == 1)
            {
                eob = k;
            }
        }

        int run = 0;
        Span<int> refinementBits = stackalloc int[JxlJpegData.DctBlockSize];
        int refinementCount = 0;
        for (int k = ss; k <= se; k++)
        {
            if (absValues[k] == 0)
            {
                run++;
                continue;
            }

            while (run > 15 && k <= eob)
            {
                Flush();
                WriteSymbol(0xF0, acTable);
                run -= 16;
                for (int i = 0; i < refinementCount; i++)
                {
                    WriteBits(1, (ulong)refinementBits[i]);
                }

                refinementCount = 0;
            }

            if (absValues[k] > 1)
            {
                refinementBits[refinementCount++] = absValues[k] & 1;
                continue;
            }

            Flush();
            int symbol = (run << 4) + 1;
            int newNonZeroBit = coefficients[(int)JxlJpegData.NaturalOrder[k]] < 0 ? 0 : 1;
            WriteSymbol(symbol, acTable);
            WriteBits(1, (ulong)newNonZeroBit);
            for (int i = 0; i < refinementCount; i++)
            {
                WriteBits(1, (ulong)refinementBits[i]);
            }

            refinementCount = 0;
            run = 0;
        }

        if (run > 0 || refinementCount > 0)
        {
            BufferEndOfBand(acTable, refinementBits[..refinementCount]);
            if (!eobRunAllowed)
            {
                Flush();
            }
        }
    }
}
