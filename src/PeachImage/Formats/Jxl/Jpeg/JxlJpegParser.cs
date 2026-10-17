namespace PeachImage.Formats.Jxl.Jpeg;

/// <summary>A parsed JPEG: its <see cref="JxlJpegData"/> and the metadata that moves out of the JPEG markers into the JPEG XL file proper.</summary>
internal sealed class JxlParsedJpeg
{
    public JxlParsedJpeg(JxlJpegData data, byte[]? iccProfile, byte[]? exif, byte[]? xmp)
    {
        Data = data;
        IccProfile = iccProfile;
        Exif = exif;
        Xmp = xmp;
    }

    public JxlJpegData Data { get; }

    /// <summary>The ICC profile assembled from the JPEG's ICC markers, or <see langword="null"/>.</summary>
    public byte[]? IccProfile { get; }

    /// <summary>The Exif payload starting at its TIFF header, or <see langword="null"/>.</summary>
    public byte[]? Exif { get; }

    /// <summary>The XMP packet, or <see langword="null"/>.</summary>
    public byte[]? Xmp { get; }
}

/// <summary>
/// Reads a baseline (sequential Huffman, 8-bit) JPEG file down to its quantized DCT coefficients, keeping everything
/// <see cref="JxlJpegWriter"/> needs to write the very same bytes back: marker order, tables, scan layout, restart
/// intervals, padding bits, stray bytes between markers and after the end of the image. The inverse of
/// <see cref="JxlJpegWriter"/>. Anything outside that scope is rejected with <see cref="JxlUnsupportedFeatureException"/>.
/// </summary>
internal static class JxlJpegParser
{
    private const int MaxDimension = 65535;
    private const int MaxInterMarkerData = 65535;
    private const int MaxQuantTables = 3;

    public static JxlParsedJpeg Parse(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            throw Unsupported("the data is not a JPEG file");
        }

        return new Parser(data).Run();
    }

    private static JxlUnsupportedFeatureException Unsupported(string what) => new($"This JPEG cannot be recompressed: {what}.");

    private sealed class Parser
    {
        private readonly byte[] _data;
        private readonly JxlJpegData _jpeg = new();
        private readonly int[] _quantById = [-1, -1, -1, -1];
        private readonly HuffmanDecoder?[] _dcTables = new HuffmanDecoder?[4];
        private readonly HuffmanDecoder?[] _acTables = new HuffmanDecoder?[4];
        private readonly List<byte> _paddingBits = [];
        private readonly bool[] _scanned = new bool[3];
        private int _pos = 2;
        private bool _seenSof;
        private bool _seenDri;
        private bool _anyZeroPadding;
        private int _adobeTransform = -1;

        // Entropy-coded bit source.
        private ulong _bits;
        private int _bitCount;

        public Parser(byte[] data)
        {
            _data = data;
        }

        public JxlParsedJpeg Run()
        {
            while (true)
            {
                int start = _pos;
                while (_pos < _data.Length && _data[_pos] != 0xFF)
                {
                    _pos++;
                }

                if (_pos > start)
                {
                    if (_pos - start > MaxInterMarkerData)
                    {
                        throw Unsupported("too much data between markers");
                    }

                    _jpeg.MarkerOrder.Add(0xFF);
                    _jpeg.InterMarkerData.Add(_data[start.._pos]);
                }

                if (_pos + 1 >= _data.Length)
                {
                    throw Unsupported("the file ends before its end-of-image marker");
                }

                byte marker = _data[_pos + 1];
                _pos += 2;
                if (marker == 0xD9)
                {
                    _jpeg.MarkerOrder.Add(0xD9);
                    _jpeg.TailData = _data[_pos..];
                    break;
                }

                ReadSegment(marker);
            }

            Finish();
            return ClassifyMetadata();
        }

        private int ReadSegmentBounds(out int end)
        {
            if (_pos + 2 > _data.Length)
            {
                throw Unsupported("truncated marker segment");
            }

            int length = (_data[_pos] << 8) | _data[_pos + 1];
            if (length < 2 || _pos + length > _data.Length)
            {
                throw Unsupported("truncated marker segment");
            }

            end = _pos + length;
            return length;
        }

        private void ReadSegment(byte marker)
        {
            switch (marker)
            {
                case 0xC0:
                case 0xC1:
                    ReadSof(marker);
                    break;
                case 0xC4:
                    ReadDht();
                    break;
                case 0xDB:
                    ReadDqt();
                    break;
                case 0xDD:
                    ReadDri();
                    break;
                case 0xDA:
                    ReadScan();
                    break;
                case >= 0xE0 and <= 0xEF:
                    ReadApp(marker);
                    break;
                case 0xFE:
                {
                    ReadSegmentBounds(out int end);
                    _jpeg.MarkerOrder.Add(0xFE);
                    _jpeg.ComData.Add(Segment(marker, end));
                    _pos = end;
                    break;
                }

                case 0xC2:
                    throw Unsupported("progressive JPEGs are not supported yet");
                default:
                    throw Unsupported($"marker 0x{marker:X2} is not supported");
            }
        }

        // The marker byte, then the segment (length field included), as stored in the reconstruction data.
        private byte[] Segment(byte marker, int end)
        {
            byte[] segment = new byte[1 + end - _pos];
            segment[0] = marker;
            _data.AsSpan(_pos, end - _pos).CopyTo(segment.AsSpan(1));
            return segment;
        }

        private void ReadApp(byte marker)
        {
            ReadSegmentBounds(out int end);
            byte[] segment = Segment(marker, end);
            _jpeg.MarkerOrder.Add(marker);
            _jpeg.AppData.Add(segment);
            _jpeg.AppMarkerTypes.Add(JpegAppMarkerType.Unknown);
            if (marker == 0xEE && segment.Length >= 3 + 12 && segment.AsSpan(3, 5).SequenceEqual("Adobe"u8))
            {
                _adobeTransform = segment[3 + 11];
            }

            _pos = end;
        }

        private void ReadSof(byte marker)
        {
            if (_seenSof)
            {
                throw Unsupported("more than one frame header");
            }

            int length = ReadSegmentBounds(out int end);
            int count = length >= 8 ? _data[_pos + 7] : 0;
            if (count is not (1 or 3) || length != 8 + (3 * count))
            {
                throw Unsupported("only one- and three-component JPEGs are supported");
            }

            if (_data[_pos + 2] != 8)
            {
                throw Unsupported("only 8-bit JPEGs are supported");
            }

            int height = (_data[_pos + 3] << 8) | _data[_pos + 4];
            int width = (_data[_pos + 5] << 8) | _data[_pos + 6];
            if (width == 0 || height == 0 || width > MaxDimension || height > MaxDimension)
            {
                throw Unsupported("invalid image size");
            }

            _jpeg.Width = width;
            _jpeg.Height = height;
            for (int i = 0; i < count; i++)
            {
                int at = _pos + 8 + (3 * i);
                var component = new JpegComponent
                {
                    Id = _data[at],
                    HorizontalSamplingFactor = _data[at + 1] >> 4,
                    VerticalSamplingFactor = _data[at + 1] & 15,
                };
                int table = _data[at + 2];
                if (table > 3 || _quantById[table] < 0)
                {
                    throw Unsupported("a component uses a quantization table that is not defined before the frame header");
                }

                component.QuantTableIndex = (uint)_quantById[table];
                foreach (var other in _jpeg.Components)
                {
                    if (other.Id == component.Id)
                    {
                        throw Unsupported("duplicate component ids");
                    }
                }

                _jpeg.Components.Add(component);
            }

            ValidateLayout();
            _jpeg.MarkerOrder.Add(marker);
            _seenSof = true;
            _pos = end;
        }

        // The sampling layouts a JPEG XL frame header can describe: gray, or Y with any of 1x1, 2x1, 1x2 and 2x2 over 1x1 chroma.
        private void ValidateLayout()
        {
            var components = _jpeg.Components;
            if (components.Count == 1)
            {
                if (components[0].HorizontalSamplingFactor != 1 || components[0].VerticalSamplingFactor != 1)
                {
                    throw Unsupported("unsupported sampling factors");
                }
            }
            else
            {
                var y = components[0];
                if (y.HorizontalSamplingFactor is < 1 or > 2 || y.VerticalSamplingFactor is < 1 or > 2)
                {
                    throw Unsupported("unsupported sampling factors");
                }

                for (int i = 1; i < 3; i++)
                {
                    if (components[i].HorizontalSamplingFactor != 1 || components[i].VerticalSamplingFactor != 1)
                    {
                        throw Unsupported("unsupported sampling factors");
                    }
                }

                if (components[0].Id == 'R' && components[1].Id == 'G' && components[2].Id == 'B')
                {
                    throw Unsupported("RGB JPEGs are not supported");
                }
            }

            int maxH = 1;
            int maxV = 1;
            foreach (var component in components)
            {
                maxH = Math.Max(maxH, component.HorizontalSamplingFactor);
                maxV = Math.Max(maxV, component.VerticalSamplingFactor);
            }

            int mcusX = (_jpeg.Width + (8 * maxH) - 1) / (8 * maxH);
            int mcusY = (_jpeg.Height + (8 * maxV) - 1) / (8 * maxV);
            foreach (var component in components)
            {
                component.WidthInBlocks = (uint)(mcusX * component.HorizontalSamplingFactor);
                component.HeightInBlocks = (uint)(mcusY * component.VerticalSamplingFactor);
                long coefficients = (long)component.WidthInBlocks * component.HeightInBlocks * JxlJpegData.DctBlockSize;
                if (coefficients > int.MaxValue / 2)
                {
                    throw Unsupported("the image is too large");
                }

                component.Coefficients = new short[coefficients];
            }
        }

        private void ReadDqt()
        {
            ReadSegmentBounds(out int end);
            _jpeg.MarkerOrder.Add(0xDB);
            int at = _pos + 2;
            int first = _jpeg.Quant.Count;
            while (at < end)
            {
                int info = _data[at++];
                int precision = info >> 4;
                int id = info & 15;
                int size = precision == 0 ? 1 : 2;
                if (precision > 1 || id > 3 || _quantById[id] >= 0 || at + (64 * size) > end || _jpeg.Quant.Count >= MaxQuantTables)
                {
                    throw Unsupported("unsupported quantization tables");
                }

                var table = new JpegQuantTable { Precision = (uint)precision, Index = (uint)id, IsLast = false };
                for (int i = 0; i < 64; i++)
                {
                    int value = size == 1 ? _data[at] : (_data[at] << 8) | _data[at + 1];
                    at += size;
                    if (value == 0)
                    {
                        throw Unsupported("a quantization value is zero");
                    }

                    table.Values[JxlJpegData.NaturalOrder[i]] = value;
                }

                _quantById[id] = _jpeg.Quant.Count;
                _jpeg.Quant.Add(table);
            }

            if (_jpeg.Quant.Count == first || at != end)
            {
                throw Unsupported("an empty or damaged quantization table segment");
            }

            _jpeg.Quant[^1].IsLast = true;
            _pos = end;
        }

        private void ReadDht()
        {
            ReadSegmentBounds(out int end);
            _jpeg.MarkerOrder.Add(0xC4);
            int at = _pos + 2;
            int first = _jpeg.HuffmanCodes.Count;
            while (at < end)
            {
                if (at + 17 > end)
                {
                    throw Unsupported("a damaged Huffman table");
                }

                int info = _data[at++];
                bool isAc = (info >> 4) == 1;
                int id = info & 15;
                if ((info >> 4) > 1 || id > 3)
                {
                    throw Unsupported("unsupported Huffman table");
                }

                var code = new JpegHuffmanCode { SlotId = (isAc ? 0x10 : 0) | id, IsLast = false };
                int total = 0;
                int maxLength = 0;
                var counts = new int[JpegHuffmanCode.MaxBitLength + 1];
                for (int i = 1; i <= JpegHuffmanCode.MaxBitLength; i++)
                {
                    counts[i] = _data[at++];
                    total += counts[i];
                    if (counts[i] != 0)
                    {
                        maxLength = i;
                    }
                }

                if (total == 0 || total > 256 || at + total > end)
                {
                    throw Unsupported("an empty or damaged Huffman table");
                }

                var symbols = new byte[total];
                _data.AsSpan(at, total).CopyTo(symbols);
                at += total;

                var decoder = HuffmanDecoder.Create(counts, symbols, isAc);

                // The code of a JPEG XL reconstruction record carries one extra, never-used symbol at the end of its longest length.
                for (int i = 1; i <= JpegHuffmanCode.MaxBitLength; i++)
                {
                    code.Counts[i] = (uint)counts[i];
                }

                code.Counts[maxLength]++;
                for (int i = 0; i < total; i++)
                {
                    code.Values[i] = symbols[i];
                }

                code.Values[total] = JpegHuffmanCode.AlphabetSize;
                (isAc ? _acTables : _dcTables)[id] = decoder;
                _jpeg.HuffmanCodes.Add(code);
            }

            if (_jpeg.HuffmanCodes.Count == first || at != end)
            {
                throw Unsupported("an empty or damaged Huffman table segment");
            }

            _jpeg.HuffmanCodes[^1].IsLast = true;
            _pos = end;
        }

        private void ReadDri()
        {
            int length = ReadSegmentBounds(out int end);
            if (length != 4 || _jpeg.Scans.Count > 0)
            {
                throw Unsupported("an unsupported restart interval definition");
            }

            uint interval = (uint)((_data[_pos + 2] << 8) | _data[_pos + 3]);
            if (_seenDri && interval != _jpeg.RestartInterval)
            {
                throw Unsupported("more than one restart interval");
            }

            _seenDri = true;
            _jpeg.RestartInterval = interval;
            _jpeg.MarkerOrder.Add(0xDD);
            _pos = end;
        }

        // ---- Scans ----------------------------------------------------------------------------------------------------

        private void ReadScan()
        {
            if (!_seenSof)
            {
                throw Unsupported("a scan before the frame header");
            }

            int length = ReadSegmentBounds(out int end);
            int count = length >= 3 ? _data[_pos + 2] : 0;
            if (count < 1 || count > _jpeg.Components.Count || length != 6 + (2 * count))
            {
                throw Unsupported("an invalid scan header");
            }

            var scan = new JpegScanInfo { NumComponents = (uint)count };
            for (int i = 0; i < count; i++)
            {
                int at = _pos + 3 + (2 * i);
                int componentIndex = -1;
                for (int c = 0; c < _jpeg.Components.Count; c++)
                {
                    if (_jpeg.Components[c].Id == _data[at])
                    {
                        componentIndex = c;
                    }
                }

                int dc = _data[at + 1] >> 4;
                int ac = _data[at + 1] & 15;
                if (componentIndex < 0 || _scanned[componentIndex] || dc > 3 || ac > 3 || _dcTables[dc] is null || _acTables[ac] is null)
                {
                    throw Unsupported("a scan uses a component twice or a Huffman table that is not defined");
                }

                _scanned[componentIndex] = true;
                scan.Components[i] = new JpegComponentScanInfo { ComponentIndex = (uint)componentIndex, DcTableIndex = (uint)dc, AcTableIndex = (uint)ac };
            }

            int tail = _pos + 3 + (2 * count);
            if (_data[tail] != 0 || _data[tail + 1] != 63 || _data[tail + 2] != 0)
            {
                throw Unsupported("only sequential scans are supported");
            }

            scan.Ss = 0;
            scan.Se = 63;
            _jpeg.Scans.Add(scan);
            _jpeg.MarkerOrder.Add(0xDA);
            _pos = end;
            DecodeScan(scan);
        }

        private void DecodeScan(JpegScanInfo scan)
        {
            _jpeg.CalculateMcuSize(scan, out int mcusPerRow, out int mcuRows);
            int restartInterval = (int)_jpeg.RestartInterval;
            bool interleaved = scan.NumComponents > 1;
            int restartsToGo = restartInterval;
            int nextRestart = 0;
            int blockScanIndex = 0;
            Span<int> lastDc = stackalloc int[JxlJpegData.MaxComponents];
            _bits = 0;
            _bitCount = 0;

            for (int mcuY = 0; mcuY < mcuRows; mcuY++)
            {
                for (int mcuX = 0; mcuX < mcusPerRow; mcuX++)
                {
                    if (restartInterval > 0 && restartsToGo == 0)
                    {
                        EndEntropySegment();
                        if (_pos + 2 > _data.Length || _data[_pos] != 0xFF || _data[_pos + 1] != 0xD0 + nextRestart)
                        {
                            throw Unsupported("a restart marker is missing or out of order");
                        }

                        _pos += 2;
                        nextRestart = (nextRestart + 1) & 7;
                        restartsToGo = restartInterval;
                        lastDc.Clear();
                    }

                    for (int i = 0; i < scan.NumComponents; i++)
                    {
                        var info = scan.Components[i];
                        var component = _jpeg.Components[(int)info.ComponentIndex];
                        var dcTable = _dcTables[info.DcTableIndex]!;
                        var acTable = _acTables[info.AcTableIndex]!;
                        int blocksY = interleaved ? component.VerticalSamplingFactor : 1;
                        int blocksX = interleaved ? component.HorizontalSamplingFactor : 1;
                        for (int iy = 0; iy < blocksY; iy++)
                        {
                            for (int ix = 0; ix < blocksX; ix++)
                            {
                                long blockY = ((long)mcuY * blocksY) + iy;
                                long blockX = ((long)mcuX * blocksX) + ix;
                                long offset = ((blockY * component.WidthInBlocks) + blockX) * JxlJpegData.DctBlockSize;
                                DecodeBlock(component.Coefficients.AsSpan((int)offset, JxlJpegData.DctBlockSize), dcTable, acTable, ref lastDc[(int)info.ComponentIndex], scan, blockScanIndex);
                                blockScanIndex++;
                            }
                        }
                    }

                    restartsToGo--;
                }
            }

            EndEntropySegment();
        }

        private void DecodeBlock(Span<short> block, HuffmanDecoder dcTable, HuffmanDecoder acTable, ref int lastDc, JpegScanInfo scan, int blockScanIndex)
        {
            int category = Decode(dcTable);
            int diff = category == 0 ? 0 : Extend(ReadBits(category), category);
            lastDc += diff;
            if (lastDc is < -2047 or > 2047)
            {
                throw Unsupported("a DC coefficient is out of range");
            }

            block[0] = (short)lastDc;
            int k = 1;
            int zeroRuns = 0;
            while (k < 64)
            {
                int symbol = Decode(acTable);
                int run = symbol >> 4;
                int size = symbol & 15;
                if (size == 0)
                {
                    if (run == 15)
                    {
                        zeroRuns++;
                        k += 16;
                        continue;
                    }

                    if (run != 0)
                    {
                        throw Unsupported("an invalid AC symbol");
                    }

                    break;
                }

                if (size > 10)
                {
                    throw Unsupported("an AC coefficient is out of range");
                }

                k += run;
                zeroRuns = 0;
                if (k > 63)
                {
                    throw Unsupported("a coefficient run overflows its block");
                }

                block[(int)JxlJpegData.NaturalOrder[k]] = (short)Extend(ReadBits(size), size);
                k++;
            }

            if (k > 64)
            {
                throw Unsupported("a coefficient run overflows its block");
            }

            if (zeroRuns > 0)
            {
                if (zeroRuns > 4)
                {
                    throw Unsupported("too many zero-run symbols at the end of a block");
                }

                scan.ExtraZeroRuns.Add(((uint)blockScanIndex, (uint)zeroRuns));
            }
        }

        private static int Extend(int value, int bits) => value < (1 << (bits - 1)) ? value - (1 << bits) + 1 : value;

        // ---- Entropy-coded bit source --------------------------------------------------------------------------------

        private void Fill()
        {
            while (_bitCount <= 48 && _pos < _data.Length)
            {
                byte b = _data[_pos];
                if (b == 0xFF)
                {
                    if (_pos + 1 >= _data.Length || _data[_pos + 1] != 0)
                    {
                        return;
                    }

                    _pos += 2;
                }
                else
                {
                    _pos++;
                }

                _bits = (_bits << 8) | b;
                _bitCount += 8;
            }
        }

        private int ReadBits(int count)
        {
            if (_bitCount < count)
            {
                Fill();
                if (_bitCount < count)
                {
                    throw Unsupported("the scan data ends early");
                }
            }

            _bitCount -= count;
            return (int)((_bits >> _bitCount) & ((1UL << count) - 1));
        }

        private int Decode(HuffmanDecoder table)
        {
            if (_bitCount < 16)
            {
                Fill();
            }

            int peek = _bitCount >= 16 ? (int)((_bits >> (_bitCount - 16)) & 0xFFFF) : (int)((_bits << (16 - _bitCount)) & 0xFFFF);
            int entry = table.Lookup[peek >> 7];
            int length;
            int symbol;
            if (entry != 0)
            {
                length = entry >> 8;
                symbol = entry & 0xFF;
            }
            else
            {
                length = 10;
                int code = peek >> 6;
                while (length <= 16 && code > table.MaxCode[length])
                {
                    length++;
                    code = peek >> (16 - length);
                }

                if (length > 16)
                {
                    throw Unsupported("an invalid Huffman code in the scan data");
                }

                symbol = table.Symbols[table.ValuePointer[length] + code - table.MinCode[length]];
            }

            if (length > _bitCount)
            {
                throw Unsupported("the scan data ends early");
            }

            _bitCount -= length;
            return symbol;
        }

        // Ends an entropy-coded segment: remembers the padding bits that complete its last byte and hands back the bytes that were read ahead.
        private void EndEntropySegment()
        {
            int padding = _bitCount & 7;
            if (padding > 0)
            {
                int pattern = (int)((_bits >> (_bitCount - padding)) & ((1UL << padding) - 1));
                for (int i = padding - 1; i >= 0; i--)
                {
                    byte bit = (byte)((pattern >> i) & 1);
                    _paddingBits.Add(bit);
                    if (bit == 0)
                    {
                        _anyZeroPadding = true;
                    }
                }
            }

            for (int whole = (_bitCount - padding) / 8; whole > 0; whole--)
            {
                _pos--;
                if (_data[_pos] == 0 && _pos > 0 && _data[_pos - 1] == 0xFF)
                {
                    _pos--;
                }
            }

            _bits = 0;
            _bitCount = 0;
        }

        // ---- Wrap-up --------------------------------------------------------------------------------------------------

        private void Finish()
        {
            if (!_seenSof || _jpeg.Scans.Count == 0)
            {
                throw Unsupported("the JPEG has no image data");
            }

            foreach (bool scanned in _scanned.AsSpan(0, _jpeg.Components.Count))
            {
                if (!scanned)
                {
                    throw Unsupported("a component is missing from the scans");
                }
            }

            if (_jpeg.Components.Count == 3 && _adobeTransform is not (-1 or 1))
            {
                throw Unsupported("the JPEG is not YCbCr");
            }

            if (_anyZeroPadding)
            {
                _jpeg.HasZeroPaddingBit = true;
                _jpeg.PaddingBits.AddRange(_paddingBits);
            }
        }

        // ICC, Exif and XMP markers are carried by the JPEG XL file itself (the codestream and its boxes); the reconstruction data only keeps their framing.
        private JxlParsedJpeg ClassifyMetadata()
        {
            var types = _jpeg.AppMarkerTypes;
            var appData = _jpeg.AppData;

            var icc = new List<int>();
            for (int i = 0; i < appData.Count; i++)
            {
                byte[] marker = appData[i];
                if (marker[0] == 0xE2 && marker.Length >= 17 && marker.AsSpan(3, 12).SequenceEqual(JxlJpegData.IccProfileTag))
                {
                    icc.Add(i);
                }
            }

            byte[]? profile = null;
            bool iccValid = icc.Count > 0;
            foreach (int i in icc)
            {
                // The reader numbers the pieces from one in file order and states their total, so only that layout survives.
                iccValid &= appData[i][15] == icc.IndexOf(i) + 1 && appData[i][16] == icc.Count;
            }

            if (iccValid)
            {
                var joined = new List<byte>();
                foreach (int i in icc)
                {
                    types[i] = JpegAppMarkerType.Icc;
                    joined.AddRange(appData[i].AsSpan(17));
                }

                profile = [.. joined];
            }

            byte[]? exif = null;
            byte[]? xmp = null;
            for (int i = 0; i < appData.Count; i++)
            {
                byte[] marker = appData[i];
                if (marker[0] != 0xE1)
                {
                    continue;
                }

                if (exif is null && marker.Length > 3 + JxlJpegData.ExifTag.Length && marker.AsSpan(3, JxlJpegData.ExifTag.Length).SequenceEqual(JxlJpegData.ExifTag))
                {
                    exif = marker[(3 + JxlJpegData.ExifTag.Length)..];
                    types[i] = JpegAppMarkerType.Exif;
                }
                else if (xmp is null && marker.Length > 3 + JxlJpegData.XmpTag.Length && marker.AsSpan(3, JxlJpegData.XmpTag.Length).SequenceEqual(JxlJpegData.XmpTag))
                {
                    xmp = marker[(3 + JxlJpegData.XmpTag.Length)..];
                    types[i] = JpegAppMarkerType.Xmp;
                }
            }

            return new JxlParsedJpeg(_jpeg, profile, exif, xmp);
        }
    }

    // A canonical Huffman decoding table: a nine-bit lookup for the common short codes and per-length bounds for the rest.
    private sealed class HuffmanDecoder
    {
        public readonly int[] Lookup = new int[512];
        public readonly int[] MaxCode = new int[18];
        public readonly int[] MinCode = new int[17];
        public readonly int[] ValuePointer = new int[17];
        public byte[] Symbols = [];

        public static HuffmanDecoder Create(int[] counts, byte[] symbols, bool isAc)
        {
            var table = new HuffmanDecoder { Symbols = symbols };
            Array.Fill(table.MaxCode, -1);
            int code = 0;
            int index = 0;
            for (int length = 1; length <= 16; length++)
            {
                table.ValuePointer[length] = index;
                table.MinCode[length] = code;
                int count = counts[length];
                if (count > 0)
                {
                    if (code + count > (1 << length))
                    {
                        throw Unsupported("an over-subscribed Huffman table");
                    }

                    for (int i = 0; i < count; i++)
                    {
                        int symbol = symbols[index + i];
                        if (!isAc && symbol > 11)
                        {
                            throw Unsupported("a DC Huffman table has symbols out of range");
                        }

                        if (length <= 9)
                        {
                            int first = (code + i) << (9 - length);
                            int last = first + (1 << (9 - length));
                            for (int fill = first; fill < last; fill++)
                            {
                                table.Lookup[fill] = (length << 8) | symbol;
                            }
                        }
                    }

                    table.MaxCode[length] = code + count - 1;
                    code += count;
                    index += count;
                }

                code <<= 1;
            }

            return table;
        }
    }
}
