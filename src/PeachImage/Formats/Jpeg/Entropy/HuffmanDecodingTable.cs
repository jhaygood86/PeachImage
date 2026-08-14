using PeachImage.Formats.Jpeg.Markers.Segments;

namespace PeachImage.Formats.Jpeg.Entropy;

/// <summary>The three things an AC Huffman symbol can mean, per ITU-T.81 F.1.2.2 — returned by <see cref="HuffmanDecodingTable.DecodeAc"/>.</summary>
internal enum AcSymbolKind : byte
{
    /// <summary>A real coefficient: <c>run</c> zero coefficients precede it, then <c>value</c> is the coefficient itself.</summary>
    Coefficient = 1,

    /// <summary>End-of-block: every remaining coefficient in this block (in this scan's spectral range) is zero.</summary>
    EndOfBlock = 2,

    /// <summary>Zero-run-length: 16 zero coefficients, no value follows.</summary>
    ZeroRun16 = 3,
}

/// <summary>
/// A JPEG Huffman decoding table, built per ITU-T.81 Annex C/F.2.2.3 from a bit-length-count array plus the
/// symbol values in code order. Most real-world Huffman codes are short (the whole point of Huffman coding
/// is that common symbols get short codes), so decoding first tries an O(1) lookup against every code of
/// length &lt;= <see cref="FastTableBits"/> via a lookahead table (the standard "fast Huffman" technique used
/// by essentially every performance-oriented JPEG decoder); only longer codes fall back to the bit-by-bit
/// walk that exactly follows the spec's reference decode procedure. <see cref="FastTableBits"/> is 10, not
/// the more commonly-cited 8: benchmarked against real decode workloads, 10 measurably beat 8 (fewer codes
/// fall through to the slow path) while 12 measured no further improvement over 10 — codes needing more
/// than 10 bits of lookahead are rare enough in practice that widening past that point mostly just grows
/// the table (and its one-time per-scan build cost) without resolving any more symbols in the fast path.
/// </summary>
/// <remarks>
/// <see cref="DecodeAc"/> and its backing <c>_fastAc*</c> arrays are a second, independent fast table
/// layered on top of the one <see cref="Decode"/> uses: for baseline AC decode, most coefficients need both
/// a Huffman-coded run/size symbol <i>and</i> a following sign-extended magnitude (the separate
/// <see cref="Entropy.JpegEntropyReader.ReceiveExtend"/> step baseline scan decoding used to always pay for
/// afterward) — when the whole thing (code + magnitude bits) fits within <see cref="FastTableBits"/>, this
/// resolves both in the one <see cref="Entropy.JpegEntropyReader.PeekBits"/>/<see cref="Entropy.JpegEntropyReader.GetBits"/>
/// pair <see cref="Decode"/> already pays for anyway, instead of a second round trip through
/// <see cref="Entropy.JpegEntropyReader.ReceiveExtend"/>. <see cref="Decode"/>'s own fast table is untouched
/// and still backs the fallback path when a symbol's code resolves fast but its magnitude doesn't fit
/// alongside it (or doesn't fit in the fast table at all).
/// </remarks>
internal sealed class HuffmanDecodingTable
{
    private const int FastTableBits = 10;
    private const int FastTableSize = 1 << FastTableBits;

    private readonly int[] _minCode = new int[17];
    private readonly int[] _maxCode = new int[17];
    private readonly int[] _valPtr = new int[17];
    private readonly byte[] _values;
    private readonly short[] _fastSymbol = new short[FastTableSize];
    private readonly byte[] _fastLength = new byte[FastTableSize];

    // See the type-level remarks: a second fast table, additive to (not a replacement for) _fastSymbol/
    // _fastLength above, resolving a whole AC run/size symbol *and* its magnitude in one lookup when both
    // fit within FastTableBits. _fastAcKind[i] == 0 (the default) means "no combined match" for every slot
    // this build never touches, so DecodeAc's fallback check needs no separate initialization pass.
    private readonly byte[] _fastAcKind = new byte[FastTableSize];
    private readonly byte[] _fastAcRun = new byte[FastTableSize];
    private readonly short[] _fastAcValue = new short[FastTableSize];
    private readonly byte[] _fastAcBits = new byte[FastTableSize];

    // Same idea as _fastAc* above, for DC decode: a DC symbol is a bare magnitude size (no run nibble),
    // so this needs only a value + total-bits pair per slot, no separate "kind" byte — _fastDcBits[i] == 0
    // unambiguously means "no combined match" (a real match's bits is always >= 1, the shortest possible
    // Huffman code), the same sentinel convention _fastLength already uses above.
    private readonly short[] _fastDcValue = new short[FastTableSize];
    private readonly byte[] _fastDcBits = new byte[FastTableSize];

    private HuffmanDecodingTable(int[] minCode, int[] maxCode, int[] valPtr, byte[] values)
    {
        _minCode = minCode;
        _maxCode = maxCode;
        _valPtr = valPtr;
        _values = values;

        Array.Fill(_fastSymbol, (short)-1);
        BuildFastTable();
        BuildAcFastTable();
        BuildDcFastTable();
    }

    /// <summary>Builds a decoding table from a parsed DHT table specification.</summary>
    public static HuffmanDecodingTable Build(JpegHuffmanTableSpec spec) => Build(spec.Counts, spec.Values);

    /// <summary>Builds a decoding table from raw bit-length counts (16 entries, for lengths 1-16) and symbol values in code order.</summary>
    public static HuffmanDecodingTable Build(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
    {
        var minCode = new int[17];
        var maxCode = new int[17];
        var valPtr = new int[17];
        Array.Fill(maxCode, -1);

        int code = 0;
        int valueIndex = 0;
        for (int length = 1; length <= 16; length++)
        {
            int count = counts[length - 1];
            if (count > 0)
            {
                valPtr[length] = valueIndex;
                minCode[length] = code;
                code += count - 1;
                maxCode[length] = code;

                // A valid Huffman code of this length must fit in `length` bits (the Kraft inequality: no
                // bit-length may claim more codes than the binary tree has room for at that depth). A
                // malformed/adversarial DHT segment can violate this, which would otherwise silently produce
                // an internally-inconsistent min/max-code table that indexes out of bounds later.
                if (maxCode[length] >= (1 << length))
                {
                    throw new JpegDecodingException($"Invalid Huffman table: bit-length {length} claims more codes than fit in {length} bits.");
                }

                valueIndex += count;
                code++;
            }

            code <<= 1;
        }

        return new HuffmanDecodingTable(minCode, maxCode, valPtr, values.ToArray());
    }

    /// <summary>Decodes the next Huffman-coded symbol from <paramref name="reader"/>.</summary>
    public int Decode(JpegEntropyReader reader)
    {
        int peeked = reader.PeekBits(FastTableBits);
        int fastLength = _fastLength[peeked];
        if (fastLength != 0)
        {
            reader.GetBits(fastLength);
            return _fastSymbol[peeked];
        }

        return DecodeSlow(reader);
    }

    /// <summary>
    /// Decodes the next baseline-scan AC symbol — run/size Huffman code plus (for <see cref="AcSymbolKind.Coefficient"/>)
    /// its sign-extended magnitude — from <paramref name="reader"/>. See the type-level remarks: resolves
    /// both in one lookup when they fit within <see cref="FastTableBits"/> together; otherwise falls back to
    /// <see cref="Decode"/> plus a separate <see cref="Entropy.JpegEntropyReader.ReceiveExtend"/>, identically
    /// to how baseline AC decode worked before this fast path existed.
    /// </summary>
    public AcSymbolKind DecodeAc(JpegEntropyReader reader, out int run, out int value)
    {
        int peeked = reader.PeekBits(FastTableBits);
        byte kind = _fastAcKind[peeked];
        if (kind != 0)
        {
            reader.GetBits(_fastAcBits[peeked]);
            run = _fastAcRun[peeked];
            value = _fastAcValue[peeked];
            return (AcSymbolKind)kind;
        }

        int runSize = Decode(reader);
        int size = runSize & 0xF;
        run = runSize >> 4;
        if (size == 0)
        {
            value = 0;
            return run == 15 ? AcSymbolKind.ZeroRun16 : AcSymbolKind.EndOfBlock;
        }

        value = reader.ReceiveExtend(size);
        return AcSymbolKind.Coefficient;
    }

    /// <summary>
    /// Decodes the next DC difference value — a Huffman-coded magnitude size plus its sign-extended
    /// magnitude — from <paramref name="reader"/>. Used by both baseline and progressive-first DC decode
    /// (identical operation in both; progressive-first applies its own successive-approximation shift
    /// afterward). Simpler than <see cref="DecodeAc"/>: a DC symbol has no run/EOB/ZRL cases to dispatch on,
    /// it's always exactly "decode a size, then read that many magnitude bits" — so resolving both in one
    /// lookup needs only a value and a total-bits-consumed pair per fast-table slot, not a kind byte too.
    /// </summary>
    public int DecodeDcDiff(JpegEntropyReader reader)
    {
        int peeked = reader.PeekBits(FastTableBits);
        byte bits = _fastDcBits[peeked];
        if (bits != 0)
        {
            reader.GetBits(bits);
            return _fastDcValue[peeked];
        }

        int size = Decode(reader);
        return reader.ReceiveExtend(size);
    }

    private int DecodeSlow(JpegEntropyReader reader)
    {
        int code = reader.GetBits(1);
        int length = 1;

        while (code > _maxCode[length])
        {
            length++;
            if (length > 16)
            {
                throw new JpegDecodingException("Invalid Huffman code encountered while decoding entropy-coded data.");
            }

            code = (code << 1) | reader.GetBits(1);
        }

        return _values[_valPtr[length] + (code - _minCode[length])];
    }

    private void BuildFastTable()
    {
        for (int length = 1; length <= FastTableBits; length++)
        {
            if (_maxCode[length] == -1)
            {
                continue;
            }

            int shift = FastTableBits - length;
            for (int code = _minCode[length]; code <= _maxCode[length]; code++)
            {
                byte symbol = _values[_valPtr[length] + (code - _minCode[length])];
                int baseIndex = code << shift;
                int fillCount = 1 << shift;
                for (int fill = 0; fill < fillCount; fill++)
                {
                    _fastSymbol[baseIndex | fill] = symbol;
                    _fastLength[baseIndex | fill] = (byte)length;
                }
            }
        }
    }

    /// <summary>
    /// Builds the second, AC-specific fast table <see cref="DecodeAc"/> uses — see the type-level remarks.
    /// Harmless-but-unused when called on a DC table (DC symbols are a bare magnitude size, not a
    /// <c>run&lt;&lt;4|size</c> pair, so this would build nonsense entries for one — but <see cref="DecodeAc"/>
    /// is never invoked on a DC table, so nothing ever reads them; not worth a table-kind flag to skip it).
    /// </summary>
    private void BuildAcFastTable()
    {
        for (int length = 1; length <= FastTableBits; length++)
        {
            if (_maxCode[length] == -1)
            {
                continue;
            }

            for (int code = _minCode[length]; code <= _maxCode[length]; code++)
            {
                byte sym = _values[_valPtr[length] + (code - _minCode[length])];
                int size = sym & 0xF;
                int run = sym >> 4;
                int codeBase = code << (FastTableBits - length);

                if (size == 0)
                {
                    // Matches BaselineScanDecoder.DecodeBlock's own check verbatim: any size-0 symbol with
                    // run != 15 is EOB, not just the standard 0x00 — a malformed/adversarial DHT can legally
                    // assign size=0 to a run other than 0, and the fallback slow path already treats it as
                    // EOB regardless, so this fast table must match that, not assume only 0x00 means EOB.
                    byte kind = (byte)(run == 15 ? AcSymbolKind.ZeroRun16 : AcSymbolKind.EndOfBlock);
                    int fillCount = 1 << (FastTableBits - length);
                    for (int fill = 0; fill < fillCount; fill++)
                    {
                        _fastAcKind[codeBase | fill] = kind;
                        _fastAcBits[codeBase | fill] = (byte)length;
                    }

                    continue;
                }

                if (length + size > FastTableBits)
                {
                    // Magnitude doesn't fit alongside the code in the fast window — leave kind=0 for every
                    // slot this code's prefix covers; DecodeAc falls back to Decode()+ReceiveExtend for these.
                    continue;
                }

                int magnitudeShift = FastTableBits - length - size;
                int magnitudeCount = 1 << size;
                int dontCareCount = 1 << magnitudeShift;
                byte totalBits = (byte)(length + size);

                for (int magnitude = 0; magnitude < magnitudeCount; magnitude++)
                {
                    short value = (short)JpegEntropyReader.ExtendSign(magnitude, size);
                    int magnitudeBase = codeBase | (magnitude << magnitudeShift);
                    for (int dontCare = 0; dontCare < dontCareCount; dontCare++)
                    {
                        int slot = magnitudeBase | dontCare;
                        _fastAcKind[slot] = (byte)AcSymbolKind.Coefficient;
                        _fastAcRun[slot] = (byte)run;
                        _fastAcValue[slot] = value;
                        _fastAcBits[slot] = totalBits;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Builds the DC-specific fast table <see cref="DecodeDcDiff"/> uses — see the type-level remarks.
    /// Also built (and equally harmless) for an AC table: interpreting an AC symbol's full
    /// <c>run&lt;&lt;4|size</c> byte as a bare DC size produces well-bounded but meaningless entries (the
    /// same <c>length + size &gt; FastTableBits</c> guard below still protects every array access, same as
    /// for a genuine DC table) — safe regardless of the interpretation, but the only reason it's fine to
    /// skip a table-kind check entirely is that <see cref="DecodeDcDiff"/> is never actually invoked on an
    /// AC table (<c>FrameDecoder</c> keeps <c>dcTables</c>/<c>acTables</c> strictly separate), so nothing
    /// ever reads whatever ends up in these slots for one.
    /// </summary>
    private void BuildDcFastTable()
    {
        for (int length = 1; length <= FastTableBits; length++)
        {
            if (_maxCode[length] == -1)
            {
                continue;
            }

            for (int code = _minCode[length]; code <= _maxCode[length]; code++)
            {
                int size = _values[_valPtr[length] + (code - _minCode[length])];
                int codeBase = code << (FastTableBits - length);
                int fillCount = 1 << (FastTableBits - length);

                if (size == 0)
                {
                    // Matches ReceiveExtend's own early return (magnitudeBits == 0 => 0) — no magnitude
                    // bits to read, the DC difference is exactly 0.
                    for (int fill = 0; fill < fillCount; fill++)
                    {
                        _fastDcValue[codeBase | fill] = 0;
                        _fastDcBits[codeBase | fill] = (byte)length;
                    }

                    continue;
                }

                if (length + size > FastTableBits)
                {
                    continue;
                }

                int magnitudeShift = FastTableBits - length - size;
                int magnitudeCount = 1 << size;
                int dontCareCount = 1 << magnitudeShift;
                byte totalBits = (byte)(length + size);

                for (int magnitude = 0; magnitude < magnitudeCount; magnitude++)
                {
                    short value = (short)JpegEntropyReader.ExtendSign(magnitude, size);
                    int magnitudeBase = codeBase | (magnitude << magnitudeShift);
                    for (int dontCare = 0; dontCare < dontCareCount; dontCare++)
                    {
                        int slot = magnitudeBase | dontCare;
                        _fastDcValue[slot] = value;
                        _fastDcBits[slot] = totalBits;
                    }
                }
            }
        }
    }
}
