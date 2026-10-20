using PeachImage.Formats.Jbig2.Internal;
using PeachImage.Formats.Shared.Arithmetic;

namespace PeachImage.Formats.Jbig2.Decoding;

/// <summary>Parses the segments of a JBIG2 stream (T.88 clause 7) and composes the first page's bitmap.</summary>
internal sealed class Jbig2Document
{
    private static readonly byte[] FileSignature = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly Dictionary<uint, Jbig2SymbolDictionary> _symbolDictionaries = [];
    private readonly Dictionary<uint, Jbig2Bitmap[]> _patternDictionaries = [];
    private readonly Dictionary<uint, Jbig2HuffmanTable> _tables = [];
    private readonly Dictionary<uint, Jbig2Bitmap> _intermediateRegions = [];

    private Jbig2Bitmap? _page;
    private bool _pageHeightUnknown;
    private byte _pageDefaultPixel;
    private int _pageDefaultOperator;
    private int _pageNumber = -1;
    private int _declaredHeight;
    private bool _pageEnded;

    private readonly struct Segment
    {
        public Segment(uint number, int type, uint pageAssociation, uint[] referredTo, ReadOnlyMemory<byte> data, bool unknownLength)
        {
            Number = number;
            Type = type;
            PageAssociation = pageAssociation;
            ReferredTo = referredTo;
            Data = data;
            UnknownLength = unknownLength;
        }

        public uint Number { get; }

        public int Type { get; }

        public uint PageAssociation { get; }

        public uint[] ReferredTo { get; }

        public ReadOnlyMemory<byte> Data { get; }

        public bool UnknownLength { get; }
    }

    private readonly record struct RegionInfo(int Width, int Height, int X, int Y, int Operator);

    public static bool HasFileSignature(ReadOnlySpan<byte> header) =>
        header.Length >= FileSignature.Length && header[..FileSignature.Length].SequenceEqual(FileSignature);

    /// <summary>Reads the first page's declared size without decoding it. Height is 0 when the page is striped with an unknown height.</summary>
    public static (int Width, int Height) ReadPageSize(ReadOnlyMemory<byte> data, bool embedded)
    {
        List<Segment> segments;
        if (embedded)
        {
            segments = [];
            ParseSequential(data, 0, segments);
        }
        else
        {
            segments = ParseFile(data);
        }

        foreach (var segment in segments)
        {
            if (segment.Type == 48)
            {
                var span = segment.Data.Span;
                if (span.Length < 19)
                {
                    break;
                }

                uint width = ReadUInt32(span, 0);
                uint height = ReadUInt32(span, 4);
                if (width == 0 || width > int.MaxValue)
                {
                    break;
                }

                return ((int)width, height == 0xFFFFFFFF ? 0 : (int)Math.Min(height, int.MaxValue));
            }
        }

        throw new Jbig2DecodingException("JBIG2 stream has no page information segment.");
    }

    /// <summary>Decodes the first page of a standalone file, or of an embedded sequence of segments preceded by optional globals.</summary>
    public static Jbig2Bitmap Decode(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> globals, bool embedded)
    {
        var document = new Jbig2Document();
        if (!embedded)
        {
            document.ProcessAll(ParseFile(data));
        }
        else
        {
            var segments = new List<Segment>();
            if (!globals.IsEmpty)
            {
                ParseSequential(globals, 0, segments);
            }

            ParseSequential(data, 0, segments);
            document.ProcessAll(segments);
        }

        return document.Finish();
    }

    private static List<Segment> ParseFile(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        if (!HasFileSignature(span) || span.Length < 9)
        {
            throw new Jbig2DecodingException("Not a JBIG2 file: the file signature is missing.");
        }

        int flags = span[8];
        bool sequential = (flags & 1) != 0;
        int offset = 9;
        if ((flags & 2) == 0)
        {
            offset += 4;
        }

        var segments = new List<Segment>();
        if (sequential)
        {
            ParseSequential(data, offset, segments);
            return segments;
        }

        // Random-access organisation: every segment header first (ending with end-of-file), then the data parts in the same order.
        var headers = new List<(uint Number, int Type, uint Page, uint[] Referred, long Length)>();
        while (offset < span.Length)
        {
            var header = ParseHeader(span, ref offset);
            headers.Add(header);
            if (header.Type == 51)
            {
                break;
            }

            if (headers.Count > Jbig2DecodingLimits.MaxSegments)
            {
                throw new Jbig2DecodingException("JBIG2 file contains too many segments.");
            }
        }

        foreach (var header in headers)
        {
            long length = header.Length;
            bool unknown = length == 0xFFFFFFFFL;
            if (unknown)
            {
                length = FindUnknownLength(span, offset);
            }

            if (length > span.Length - offset)
            {
                throw new Jbig2DecodingException("JBIG2 segment data extends past the end of the file.");
            }

            segments.Add(new Segment(header.Number, header.Type, header.Page, header.Referred, data.Slice(offset, (int)length), unknown));
            offset += (int)length;
        }

        return segments;
    }

    private static void ParseSequential(ReadOnlyMemory<byte> data, int offset, List<Segment> segments)
    {
        var span = data.Span;
        while (offset < span.Length)
        {
            // Trailing zero padding is tolerated.
            if (span.Length - offset < 11)
            {
                break;
            }

            var header = ParseHeader(span, ref offset);
            long length = header.Length;
            bool unknown = length == 0xFFFFFFFFL;
            if (unknown)
            {
                length = FindUnknownLength(span, offset);
            }

            if (length > span.Length - offset)
            {
                // Some producers write a bogus length for the last segment; keep what is there.
                length = span.Length - offset;
            }

            segments.Add(new Segment(header.Number, header.Type, header.Page, header.Referred, data.Slice(offset, (int)length), unknown));
            offset += (int)length;
            if (segments.Count > Jbig2DecodingLimits.MaxSegments)
            {
                throw new Jbig2DecodingException("JBIG2 stream contains too many segments.");
            }

            if (header.Type == 51)
            {
                break;
            }
        }
    }

    private static (uint Number, int Type, uint Page, uint[] Referred, long Length) ParseHeader(ReadOnlySpan<byte> span, ref int offset)
    {
        if (span.Length - offset < 6)
        {
            throw new Jbig2DecodingException("JBIG2 segment header is truncated.");
        }

        uint number = ReadUInt32(span, offset);
        int flags = span[offset + 4];
        int type = flags & 0x3F;
        bool largePage = (flags & 0x40) != 0;
        offset += 5;

        int countByte = span[offset];
        long referredCount;
        if ((countByte >> 5) == 7)
        {
            if (span.Length - offset < 4)
            {
                throw new Jbig2DecodingException("JBIG2 segment header is truncated.");
            }

            referredCount = ReadUInt32(span, offset) & 0x1FFFFFFF;
            offset += 4 + (int)((referredCount + 8) / 8);
        }
        else
        {
            referredCount = countByte >> 5;
            offset += 1;
        }

        int referenceSize = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
        if (referredCount * referenceSize > span.Length - offset || referredCount > Jbig2DecodingLimits.MaxSegments)
        {
            throw new Jbig2DecodingException("JBIG2 segment header is truncated.");
        }

        var referred = new uint[referredCount];
        for (int i = 0; i < referred.Length; i++)
        {
            referred[i] = referenceSize switch
            {
                1 => span[offset],
                2 => (uint)((span[offset] << 8) | span[offset + 1]),
                _ => ReadUInt32(span, offset),
            };
            offset += referenceSize;
        }

        int pageSize = largePage ? 4 : 1;
        if (span.Length - offset < pageSize + 4)
        {
            throw new Jbig2DecodingException("JBIG2 segment header is truncated.");
        }

        uint page = largePage ? ReadUInt32(span, offset) : span[offset];
        offset += pageSize;
        long length = ReadUInt32(span, offset);
        offset += 4;
        return (number, type, page, referred, length);
    }

    /// <summary>For an immediate generic region of unknown length: finds the end marker (T.88 7.2.7) and returns the data length including the row-count field.</summary>
    private static long FindUnknownLength(ReadOnlySpan<byte> span, int dataStart)
    {
        if (span.Length - dataStart < 18)
        {
            throw new Jbig2DecodingException("JBIG2 segment of unknown length is truncated.");
        }

        bool mmr = (span[dataStart + 17] & 1) != 0;
        byte first = mmr ? (byte)0x00 : (byte)0xFF;
        byte second = mmr ? (byte)0x00 : (byte)0xAC;
        for (int i = dataStart + 18; i + 1 < span.Length; i++)
        {
            if (span[i] == first && span[i + 1] == second)
            {
                return i + 2 + 4 - dataStart;
            }
        }

        throw new Jbig2DecodingException("JBIG2 segment of unknown length has no end marker.");
    }

    private void ProcessAll(List<Segment> segments)
    {
        foreach (var segment in segments)
        {
            if (_pageEnded)
            {
                break;
            }

            if (segment.PageAssociation != 0 && _pageNumber >= 0 && segment.PageAssociation != _pageNumber)
            {
                continue;
            }

            Process(segment);
        }
    }

    private void Process(Segment segment)
    {
        var data = segment.Data.Span;
        switch (segment.Type)
        {
            case 0:
                _symbolDictionaries[segment.Number] = DecodeSymbolDictionary(segment, data);
                break;
            case 4:
            case 6:
            case 7:
                DecodeTextRegion(segment, data);
                break;
            case 16:
                _patternDictionaries[segment.Number] = Jbig2Halftone.DecodePatternDictionary(data);
                break;
            case 20:
            case 22:
            case 23:
                DecodeHalftoneRegion(segment, data);
                break;
            case 36:
            case 38:
            case 39:
                DecodeGenericRegion(segment, data);
                break;
            case 40:
            case 42:
            case 43:
                DecodeRefinementRegion(segment, data);
                break;
            case 48:
                DecodePageInformation(segment, data);
                break;
            case 49:
                if (_pageNumber >= 0 && segment.PageAssociation == _pageNumber)
                {
                    _pageEnded = true;
                }

                break;
            case 50:
                if (data.Length >= 4 && _page is not null && _pageHeightUnknown)
                {
                    long end = (long)ReadUInt32(data, 0) + 1;
                    if (end > _page.Height)
                    {
                        _page.SetHeight((int)Math.Min(end, int.MaxValue), _pageDefaultPixel);
                    }
                }

                break;
            case 51:
                _pageEnded = true;
                break;
            case 53:
                _tables[segment.Number] = Jbig2HuffmanTable.FromSegment(data);
                break;
            case 52:
            case 62:
                break;
            default:
                throw new Jbig2UnsupportedFeatureException($"JBIG2 segment type {segment.Type} is not supported.");
        }
    }

    private Jbig2Bitmap Finish()
    {
        if (_page is null)
        {
            throw new Jbig2DecodingException("JBIG2 stream has no page information segment.");
        }

        if (_pageHeightUnknown && _page.Height == 0)
        {
            throw new Jbig2DecodingException("JBIG2 page has no content.");
        }

        return _page;
    }

    private void DecodePageInformation(Segment segment, ReadOnlySpan<byte> data)
    {
        if (_pageNumber >= 0)
        {
            return; // Only the first page is decoded.
        }

        if (data.Length < 19)
        {
            throw new Jbig2DecodingException("JBIG2 page information segment is truncated.");
        }

        uint width = ReadUInt32(data, 0);
        uint height = ReadUInt32(data, 4);
        int flags = data[16];
        _pageDefaultPixel = (byte)((flags >> 2) & 1);
        _pageDefaultOperator = (flags >> 3) & 3;
        _pageNumber = (int)segment.PageAssociation;
        if (width == 0 || width > int.MaxValue)
        {
            throw new Jbig2DecodingException("JBIG2 page width is invalid.");
        }

        _pageHeightUnknown = height == 0xFFFFFFFF;
        _declaredHeight = _pageHeightUnknown ? 0 : (int)Math.Min(height, int.MaxValue);
        _page = new Jbig2Bitmap((int)width, _declaredHeight, _pageDefaultPixel);
    }

    private static RegionInfo ParseRegionInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length < 17)
        {
            throw new Jbig2DecodingException("JBIG2 region segment is truncated.");
        }

        uint width = ReadUInt32(data, 0);
        uint height = ReadUInt32(data, 4);
        int x = (int)ReadUInt32(data, 8);
        int y = (int)ReadUInt32(data, 12);
        if (width > int.MaxValue || height > int.MaxValue || (long)width * height > Jbig2DecodingLimits.MaxPixelCount)
        {
            throw new Jbig2DecodingException("JBIG2 region size is out of range.");
        }

        int op = data.Length > 16 ? data[16] & 7 : 0;
        if (op > 4)
        {
            throw new Jbig2DecodingException("JBIG2 region has an invalid combination operator.");
        }

        return new RegionInfo((int)width, (int)height, x, y, op);
    }

    private void Emit(Segment segment, RegionInfo info, Jbig2Bitmap bitmap, bool immediate)
    {
        if (!immediate)
        {
            _intermediateRegions[segment.Number] = bitmap;
            return;
        }

        if (_page is null)
        {
            throw new Jbig2DecodingException("JBIG2 region appears before page information.");
        }

        if (_pageHeightUnknown)
        {
            long needed = (long)info.Y + bitmap.Height;
            if (needed > _page.Height)
            {
                _page.SetHeight((int)Math.Min(needed, int.MaxValue), _pageDefaultPixel);
            }
        }

        _page.Combine(bitmap, info.X, info.Y, info.Operator);
    }

    private static bool IsImmediate(int type) => type is 6 or 7 or 22 or 23 or 38 or 39 or 42 or 43;

    private void DecodeGenericRegion(Segment segment, ReadOnlySpan<byte> data)
    {
        var info = ParseRegionInfo(data);
        if (data.Length < 18)
        {
            throw new Jbig2DecodingException("JBIG2 generic region segment is truncated.");
        }

        int flags = data[17];
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        bool typical = (flags & 8) != 0;
        if ((flags & 16) != 0)
        {
            throw new Jbig2UnsupportedFeatureException("JBIG2 extended generic templates (12 adaptive pixels) are not supported.");
        }

        int offset = 18;
        sbyte[] adaptive = [];
        if (!mmr)
        {
            int pairs = template == 0 ? 4 : 1;
            if (data.Length < offset + (pairs * 2))
            {
                throw new Jbig2DecodingException("JBIG2 generic region segment is truncated.");
            }

            adaptive = new sbyte[pairs * 2];
            for (int i = 0; i < adaptive.Length; i++)
            {
                adaptive[i] = (sbyte)data[offset + i];
            }

            offset += adaptive.Length;
        }

        var payload = data[offset..];
        if (segment.UnknownLength && payload.Length >= 4)
        {
            // The final four bytes hold the actual row count.
            uint rows = ReadUInt32(payload, payload.Length - 4);
            payload = payload[..^4];
            if (rows < info.Height)
            {
                info = info with { Height = (int)rows };
            }
        }

        Jbig2Bitmap bitmap;
        if (mmr)
        {
            bitmap = Jbig2GenericRegion.DecodeMmr(payload, info.Width, info.Height, out _);
        }
        else
        {
            int size = Jbig2GenericRegion.ContextSize(template);
            byte[] contexts = Jbig2ArithmeticContexts.Rent(size);
            try
            {
                var decoder = new MqDecoder(payload);
                bitmap = Jbig2GenericRegion.DecodeArithmetic(ref decoder, contexts.AsSpan(0, size), template, typical, adaptive, info.Width, info.Height, null);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(contexts);
            }
        }

        Emit(segment, info, bitmap, IsImmediate(segment.Type));
    }

    private void DecodeRefinementRegion(Segment segment, ReadOnlySpan<byte> data)
    {
        var info = ParseRegionInfo(data);
        if (data.Length < 18)
        {
            throw new Jbig2DecodingException("JBIG2 refinement region segment is truncated.");
        }

        int flags = data[17];
        int template = flags & 1;
        bool typical = (flags & 2) != 0;
        int offset = 18;
        sbyte[] adaptive = [];
        if (template == 0)
        {
            if (data.Length < offset + 4)
            {
                throw new Jbig2DecodingException("JBIG2 refinement region segment is truncated.");
            }

            adaptive = [(sbyte)data[offset], (sbyte)data[offset + 1], (sbyte)data[offset + 2], (sbyte)data[offset + 3]];
            offset += 4;
        }

        Jbig2Bitmap reference;
        int dx = 0;
        int dy = 0;
        Jbig2Bitmap? referred = null;
        foreach (uint number in segment.ReferredTo)
        {
            if (_intermediateRegions.TryGetValue(number, out var found))
            {
                referred = found;
            }
        }

        if (referred is not null)
        {
            reference = referred;
        }
        else
        {
            if (_page is null)
            {
                throw new Jbig2DecodingException("JBIG2 refinement region appears before page information.");
            }

            reference = _page.Extract(info.X, info.Y, info.Width, info.Height);
        }

        int size = Jbig2RefinementRegion.ContextSize(template);
        byte[] contexts = Jbig2ArithmeticContexts.Rent(size);
        try
        {
            var decoder = new MqDecoder(data[offset..]);
            var bitmap = Jbig2RefinementRegion.Decode(ref decoder, contexts.AsSpan(0, size), template, typical, adaptive, info.Width, info.Height, reference, dx, dy);
            Emit(segment, info, bitmap, IsImmediate(segment.Type));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(contexts);
        }
    }

    private void DecodeHalftoneRegion(Segment segment, ReadOnlySpan<byte> data)
    {
        var info = ParseRegionInfo(data);
        Jbig2Bitmap[]? patterns = null;
        foreach (uint number in segment.ReferredTo)
        {
            if (_patternDictionaries.TryGetValue(number, out var found))
            {
                patterns = found;
            }
        }

        if (patterns is null)
        {
            throw new Jbig2DecodingException("JBIG2 halftone region does not refer to a pattern dictionary.");
        }

        var bitmap = Jbig2Halftone.DecodeHalftoneRegion(data[17..], info.Width, info.Height, patterns);
        Emit(segment, info, bitmap, IsImmediate(segment.Type));
    }

    private Jbig2SymbolDictionary DecodeSymbolDictionary(Segment segment, ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            throw new Jbig2DecodingException("JBIG2 symbol dictionary segment is truncated.");
        }

        int flags = (data[0] << 8) | data[1];
        bool huffman = (flags & 1) != 0;
        bool refinement = (flags & 2) != 0;
        int deltaHeightSelection = (flags >> 2) & 3;
        int deltaWidthSelection = (flags >> 4) & 3;
        bool customBitmapSize = (flags & 0x40) != 0;
        bool customAggregate = (flags & 0x80) != 0;
        bool contextUsed = (flags & 0x100) != 0;
        bool contextRetained = (flags & 0x200) != 0;
        int template = (flags >> 10) & 3;
        int refinementTemplate = (flags >> 12) & 1;
        int offset = 2;

        sbyte[] adaptive = [];
        if (!huffman)
        {
            int pairs = template == 0 ? 4 : 1;
            if (data.Length < offset + (pairs * 2))
            {
                throw new Jbig2DecodingException("JBIG2 symbol dictionary segment is truncated.");
            }

            adaptive = new sbyte[pairs * 2];
            for (int i = 0; i < adaptive.Length; i++)
            {
                adaptive[i] = (sbyte)data[offset + i];
            }

            offset += adaptive.Length;
        }

        sbyte[] refinementAdaptive = [];
        if (refinement && refinementTemplate == 0)
        {
            if (data.Length < offset + 4)
            {
                throw new Jbig2DecodingException("JBIG2 symbol dictionary segment is truncated.");
            }

            refinementAdaptive = [(sbyte)data[offset], (sbyte)data[offset + 1], (sbyte)data[offset + 2], (sbyte)data[offset + 3]];
            offset += 4;
        }

        if (data.Length < offset + 8)
        {
            throw new Jbig2DecodingException("JBIG2 symbol dictionary segment is truncated.");
        }

        int exported = (int)Math.Min(ReadUInt32(data, offset), int.MaxValue);
        int created = (int)Math.Min(ReadUInt32(data, offset + 4), int.MaxValue);
        offset += 8;

        var input = new List<Jbig2Bitmap>();
        Jbig2SymbolDictionary? lastDictionary = null;
        var customTables = new Queue<Jbig2HuffmanTable>();
        foreach (uint number in segment.ReferredTo)
        {
            if (_symbolDictionaries.TryGetValue(number, out var dictionary))
            {
                input.AddRange(dictionary.Exported);
                lastDictionary = dictionary;
            }
            else if (_tables.TryGetValue(number, out var table))
            {
                customTables.Enqueue(table);
            }
        }

        Jbig2HuffmanTable? deltaHeight = null, deltaWidth = null, bitmapSize = null, aggregate = null;
        if (huffman)
        {
            deltaHeight = deltaHeightSelection switch
            {
                0 => Jbig2HuffmanTable.Standard(4),
                1 => Jbig2HuffmanTable.Standard(5),
                3 => NextCustom(customTables),
                _ => throw new Jbig2DecodingException("JBIG2 symbol dictionary has an invalid Huffman table selection."),
            };
            deltaWidth = deltaWidthSelection switch
            {
                0 => Jbig2HuffmanTable.Standard(2),
                1 => Jbig2HuffmanTable.Standard(3),
                3 => NextCustom(customTables),
                _ => throw new Jbig2DecodingException("JBIG2 symbol dictionary has an invalid Huffman table selection."),
            };
            bitmapSize = customBitmapSize ? NextCustom(customTables) : Jbig2HuffmanTable.Standard(1);
            aggregate = customAggregate ? NextCustom(customTables) : Jbig2HuffmanTable.Standard(1);
        }

        var parameters = new Jbig2SymbolDictionaryParameters
        {
            Huffman = huffman,
            RefineOrAggregate = refinement,
            Template = template,
            RefinementTemplate = refinementTemplate,
            Adaptive = adaptive,
            RefinementAdaptive = refinementAdaptive,
            ExportedCount = exported,
            NewCount = created,
            RetainContexts = contextRetained,
            DeltaHeightTable = deltaHeight,
            DeltaWidthTable = deltaWidth,
            BitmapSizeTable = bitmapSize,
            AggregateInstancesTable = aggregate,
        };

        return Jbig2SymbolDictionaryDecoder.Decode(
            parameters,
            input,
            data[offset..],
            contextUsed ? lastDictionary?.GenericContexts : null,
            contextUsed ? lastDictionary?.RefinementContexts : null);
    }

    private static Jbig2HuffmanTable NextCustom(Queue<Jbig2HuffmanTable> tables) =>
        tables.Count > 0 ? tables.Dequeue() : throw new Jbig2DecodingException("JBIG2 segment refers to a custom Huffman table that was not supplied.");

    private void DecodeTextRegion(Segment segment, ReadOnlySpan<byte> data)
    {
        var info = ParseRegionInfo(data);
        if (data.Length < 19)
        {
            throw new Jbig2DecodingException("JBIG2 text region segment is truncated.");
        }

        int flags = (data[17] << 8) | data[18];
        int offset = 19;
        bool huffman = (flags & 1) != 0;
        bool refine = (flags & 2) != 0;
        int logStrips = (flags >> 2) & 3;
        int corner = (flags >> 4) & 3;
        bool transposed = (flags & 0x40) != 0;
        int combination = (flags >> 7) & 3;
        int defaultPixel = (flags >> 9) & 1;
        int dsOffset = (flags >> 10) & 0x1F;
        if (dsOffset > 15)
        {
            dsOffset -= 32;
        }

        int refinementTemplate = (flags >> 15) & 1;

        int huffmanFlags = 0;
        if (huffman)
        {
            if (data.Length < offset + 2)
            {
                throw new Jbig2DecodingException("JBIG2 text region segment is truncated.");
            }

            huffmanFlags = (data[offset] << 8) | data[offset + 1];
            offset += 2;
        }

        sbyte[] refinementAdaptive = [];
        if (refine && refinementTemplate == 0)
        {
            if (data.Length < offset + 4)
            {
                throw new Jbig2DecodingException("JBIG2 text region segment is truncated.");
            }

            refinementAdaptive = [(sbyte)data[offset], (sbyte)data[offset + 1], (sbyte)data[offset + 2], (sbyte)data[offset + 3]];
            offset += 4;
        }

        if (data.Length < offset + 4)
        {
            throw new Jbig2DecodingException("JBIG2 text region segment is truncated.");
        }

        uint instanceCount = ReadUInt32(data, offset);
        offset += 4;

        var symbols = new List<Jbig2Bitmap>();
        var customTables = new Queue<Jbig2HuffmanTable>();
        foreach (uint number in segment.ReferredTo)
        {
            if (_symbolDictionaries.TryGetValue(number, out var dictionary))
            {
                symbols.AddRange(dictionary.Exported);
            }
            else if (_tables.TryGetValue(number, out var table))
            {
                customTables.Enqueue(table);
            }
        }

        int codeLength = 0;
        while ((1L << codeLength) < symbols.Count)
        {
            codeLength++;
        }

        var payload = data[offset..];
        var region = ParseTextRegionTables(huffmanFlags, huffman, refine, customTables);
        Jbig2HuffmanTable? symbolCodes = null;
        var reader = huffman ? new Jbig2BitReader(payload) : default;
        if (huffman)
        {
            symbolCodes = ReadSymbolCodeTable(ref reader, symbols.Count);
        }

        var parameters = new Jbig2TextRegionParameters
        {
            Huffman = huffman,
            Refine = refine,
            Width = info.Width,
            Height = info.Height,
            InstanceCount = (int)Math.Min(instanceCount, int.MaxValue),
            Strips = 1 << logStrips,
            LogStrips = logStrips,
            Symbols = symbols,
            SymbolCodeLength = codeLength,
            SymbolCodes = symbolCodes,
            DefaultPixel = defaultPixel,
            CombinationOperator = combination,
            Transposed = transposed,
            ReferenceCorner = corner,
            DeltaSOffset = dsOffset,
            RefinementTemplate = refinementTemplate,
            RefinementAdaptive = refinementAdaptive,
            FirstS = region.FirstS,
            DeltaS = region.DeltaS,
            DeltaT = region.DeltaT,
            RefinementDeltaWidth = region.RefinementDeltaWidth,
            RefinementDeltaHeight = region.RefinementDeltaHeight,
            RefinementDeltaX = region.RefinementDeltaX,
            RefinementDeltaY = region.RefinementDeltaY,
            RefinementSize = region.RefinementSize,
        };

        int refinementSize = Jbig2RefinementRegion.ContextSize(refinementTemplate);
        byte[] refinementContexts = Jbig2ArithmeticContexts.Rent(refinementSize);
        try
        {
            Jbig2Bitmap bitmap;
            if (huffman)
            {
                var unused = default(MqDecoder);
                bitmap = Jbig2TextRegion.Decode(parameters, ref unused, ref reader, null, refinementContexts.AsSpan(0, refinementSize));
            }
            else
            {
                using var arithmetic = new Jbig2ArithmeticContexts(codeLength);
                var decoder = new MqDecoder(payload);
                var unusedReader = default(Jbig2BitReader);
                bitmap = Jbig2TextRegion.Decode(parameters, ref decoder, ref unusedReader, arithmetic, refinementContexts.AsSpan(0, refinementSize));
            }

            Emit(segment, info, bitmap, IsImmediate(segment.Type));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(refinementContexts);
        }
    }

    private readonly record struct TextTables(
        Jbig2HuffmanTable? FirstS,
        Jbig2HuffmanTable? DeltaS,
        Jbig2HuffmanTable? DeltaT,
        Jbig2HuffmanTable? RefinementDeltaWidth,
        Jbig2HuffmanTable? RefinementDeltaHeight,
        Jbig2HuffmanTable? RefinementDeltaX,
        Jbig2HuffmanTable? RefinementDeltaY,
        Jbig2HuffmanTable? RefinementSize);

    private static TextTables ParseTextRegionTables(int flags, bool huffman, bool refine, Queue<Jbig2HuffmanTable> custom)
    {
        if (!huffman)
        {
            return default;
        }

        static Jbig2HuffmanTable Select(int selection, Queue<Jbig2HuffmanTable> custom, params int[] standard) =>
            selection == 3 ? NextCustom(custom)
            : selection < standard.Length ? Jbig2HuffmanTable.Standard(standard[selection])
            : throw new Jbig2DecodingException("JBIG2 text region has an invalid Huffman table selection.");

        var firstS = Select(flags & 3, custom, 6, 7);
        var deltaS = Select((flags >> 2) & 3, custom, 8, 9, 10);
        var deltaT = Select((flags >> 4) & 3, custom, 11, 12, 13);
        Jbig2HuffmanTable? rdw = null, rdh = null, rdx = null, rdy = null, rsize = null;
        if (refine)
        {
            rdw = Select((flags >> 6) & 3, custom, 14, 15);
            rdh = Select((flags >> 8) & 3, custom, 14, 15);
            rdx = Select((flags >> 10) & 3, custom, 14, 15);
            rdy = Select((flags >> 12) & 3, custom, 14, 15);
            rsize = ((flags >> 14) & 1) != 0 ? NextCustom(custom) : Jbig2HuffmanTable.Standard(1);
        }

        return new TextTables(firstS, deltaS, deltaT, rdw, rdh, rdx, rdy, rsize);
    }

    /// <summary>Reads the symbol ID Huffman table that opens a Huffman-coded text region's data (T.88 7.4.3.1.7).</summary>
    private static Jbig2HuffmanTable ReadSymbolCodeTable(ref Jbig2BitReader reader, int symbolCount)
    {
        Span<int> runLengths = stackalloc int[35];
        for (int i = 0; i < 35; i++)
        {
            runLengths[i] = (int)reader.ReadBits(4);
        }

        var runCodes = Jbig2HuffmanTable.FromPrefixLengths(runLengths);
        var lengths = new int[symbolCount];
        int index = 0;
        while (index < symbolCount)
        {
            int code = runCodes.DecodeRequired(ref reader);
            if (code < 32)
            {
                lengths[index++] = code;
                continue;
            }

            int repeat;
            int value = 0;
            if (code == 32)
            {
                if (index == 0)
                {
                    throw new Jbig2DecodingException("JBIG2 symbol code table repeats a length that does not exist.");
                }

                repeat = (int)reader.ReadBits(2) + 3;
                value = lengths[index - 1];
            }
            else if (code == 33)
            {
                repeat = (int)reader.ReadBits(3) + 3;
            }
            else
            {
                repeat = (int)reader.ReadBits(7) + 11;
            }

            if (repeat > symbolCount - index)
            {
                throw new Jbig2DecodingException("JBIG2 symbol code table overruns its symbol count.");
            }

            Array.Fill(lengths, value, index, repeat);
            index += repeat;
        }

        reader.AlignToByte();
        return Jbig2HuffmanTable.FromPrefixLengths(lengths);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
