using System.Buffers.Binary;
using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Entropy;

namespace PeachImage.Formats.Jxl.Headers;

/// <summary>
/// Decodes the ICC profile embedded in a codestream: an entropy-coded byte stream (ANS or prefix, with a
/// byte-class context model) that is a prediction residual, which <see cref="Unpredict"/> expands back to the
/// original profile via header prediction, tag-table commands, shuffles and linear predictors.
/// </summary>
internal static class JxlIccDecoder
{
    private const int HeaderSize = 128;
    private const int NumContexts = 41;
    private const ulong MaxEncodedSize = 268_435_456;
    private const int OutputLimit = 1 << 28;

    private const int CommandInsert = 1;
    private const int CommandShuffle2 = 2;
    private const int CommandShuffle4 = 3;
    private const int CommandPredict = 4;
    private const int CommandXyz = 10;
    private const int CommandTypeStartFirst = 16;
    private const int CommandTagUnknown = 1;
    private const int CommandTagTrc = 2;
    private const int CommandTagXyz = 3;
    private const int CommandTagStringFirst = 4;
    private const int FlagBitOffset = 64;
    private const int FlagBitSize = 128;

    private static readonly uint[] TagStrings =
    [
        Fourcc("cprt"), Fourcc("wtpt"), Fourcc("bkpt"), Fourcc("rXYZ"), Fourcc("gXYZ"), Fourcc("bXYZ"),
        Fourcc("kXYZ"), Fourcc("rTRC"), Fourcc("gTRC"), Fourcc("bTRC"), Fourcc("kTRC"), Fourcc("chad"),
        Fourcc("desc"), Fourcc("chrm"), Fourcc("dmnd"), Fourcc("dmdd"), Fourcc("lumi"),
    ];

    private static readonly uint[] TypeStrings =
    [
        Fourcc("XYZ "), Fourcc("desc"), Fourcc("text"), Fourcc("mluc"),
        Fourcc("para"), Fourcc("curv"), Fourcc("sf32"), Fourcc("gbd "),
    ];

    private static readonly byte[] InitialHeaderPrediction =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, (byte)'m', (byte)'n', (byte)'t', (byte)'r',
        (byte)'R', (byte)'G', (byte)'B', (byte)' ', (byte)'X', (byte)'Y', (byte)'Z', (byte)' ', 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, (byte)'a', (byte)'c', (byte)'s', (byte)'p', 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 246, 214, 0, 1, 0, 0, 0, 0, 211, 45,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary>Reads the entropy-coded ICC stream from <paramref name="br"/> and returns the original profile.</summary>
    public static byte[] Read(ref JxlBitReader br)
    {
        ulong encodedSize = JxlFieldReader.ReadU64(ref br);
        if (encodedSize > MaxEncodedSize)
        {
            throw new JxlDecodingException("The encoded ICC profile is too large.");
        }

        var code = JxlEntropyCode.Read(ref br, NumContexts, out byte[] contextMap);
        using var reader = new JxlSymbolReader(code, ref br);
        long startBit = br.BitPosition;

        int size = (int)encodedSize;
        byte[] encoded = new byte[Math.Min(size, 0x400)];
        for (int i = 0; i < size; i++)
        {
            if (i == encoded.Length)
            {
                // Grow in steps so a tiny hostile stream can't force a huge allocation up front.
                Array.Resize(ref encoded, (int)Math.Min((long)size, (long)encoded.Length * 2));
            }

            if ((i & 0xFFFF) == 0 && i > 0)
            {
                double usedBytes = (br.BitPosition - startBit) / 8.0;
                if (i > usedBytes * 256)
                {
                    throw new JxlDecodingException("Corrupted ICC stream.");
                }
            }

            byte b1 = i > 0 ? encoded[i - 1] : (byte)0;
            byte b2 = i > 1 ? encoded[i - 2] : (byte)0;
            encoded[i] = (byte)reader.ReadHybridUint(contextMap[Context(i, b1, b2)], ref br);
            if ((i & 0x3FF) == 0x3FF)
            {
                br.ThrowIfOverrun();
            }
        }

        br.ThrowIfOverrun();
        if (!reader.CheckFinalState())
        {
            throw new JxlDecodingException("Corrupted ICC profile.");
        }

        return Unpredict(encoded.AsSpan(0, size));
    }

    private static int Context(int i, byte b1, byte b2) => i <= 128 ? 0 : 1 + ByteKind1(b1) + (ByteKind2(b2) * 8);

    private static int ByteKind1(byte b)
    {
        if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z')
        {
            return 0;
        }

        if (b is >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)',')
        {
            return 1;
        }

        if (b == 0)
        {
            return 2;
        }

        if (b == 1)
        {
            return 3;
        }

        if (b < 16)
        {
            return 4;
        }

        if (b == 255)
        {
            return 6;
        }

        return b > 240 ? 5 : 7;
    }

    private static int ByteKind2(byte b)
    {
        if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z')
        {
            return 0;
        }

        if (b is >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)',')
        {
            return 1;
        }

        if (b < 16)
        {
            return 2;
        }

        return b > 240 ? 3 : 4;
    }

    /// <summary>Expands the predicted/commanded form back into the original ICC profile.</summary>
    internal static byte[] Unpredict(ReadOnlySpan<byte> enc)
    {
        int size = enc.Length;
        int pos = 0;
        ulong outputSize = DecodeVarInt(enc, size, ref pos);
        CheckIs32Bit(outputSize);
        ulong commandsSize = DecodeVarInt(enc, size, ref pos);
        CheckIs32Bit(commandsSize);
        int cpos = pos;
        CheckBounds((ulong)pos, commandsSize, (ulong)size);
        int commandsEnd = cpos + (int)commandsSize;
        pos = commandsEnd;

        if (outputSize > OutputLimit)
        {
            throw new JxlDecodingException("The decoded ICC profile is too large.");
        }

        // The profile is built in place; its length can never exceed the declared output size.
        var result = new IccBuffer((int)outputSize);

        // Header: predicted from a fixed prototype, residual added byte by byte.
        byte[] header = (byte[])InitialHeaderPrediction.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)outputSize);
        for (int i = 0; i <= HeaderSize; i++)
        {
            if (result.Count == (int)outputSize)
            {
                if (cpos != commandsEnd)
                {
                    throw new JxlDecodingException("Not all ICC commands were used.");
                }

                if (pos != size)
                {
                    throw new JxlDecodingException("Not all ICC data was used.");
                }

                return result.ToArray();
            }

            if (i == HeaderSize)
            {
                break;
            }

            PredictHeader(result.Span, header, i);
            if (pos >= size)
            {
                throw new JxlDecodingException("ICC data out of bounds.");
            }

            result.Add((byte)(enc[pos++] + header[i]));
        }

        if (cpos >= commandsEnd)
        {
            throw new JxlDecodingException("ICC commands out of bounds.");
        }

        // Tag list.
        ulong numTags = DecodeVarInt(enc, commandsEnd, ref cpos);
        if (numTags != 0)
        {
            numTags--;
            CheckIs32Bit(numTags);
            result.AddUInt32((uint)numTags);
            ulong previousTagStart = HeaderSize + (numTags * 12);
            ulong previousTagSize = 0;
            while (true)
            {
                if (result.Count > (int)outputSize || cpos > commandsEnd)
                {
                    throw new JxlDecodingException("Invalid ICC result size.");
                }

                if (cpos == commandsEnd)
                {
                    break;
                }

                byte command = enc[cpos++];
                int tagCode = command & 63;
                uint tag;
                if (tagCode == 0)
                {
                    break;
                }

                if (tagCode == CommandTagUnknown)
                {
                    CheckBounds((ulong)pos, 4, (ulong)size);
                    tag = BinaryPrimitives.ReadUInt32BigEndian(enc.Slice(pos, 4));
                    pos += 4;
                }
                else if (tagCode == CommandTagTrc)
                {
                    tag = Fourcc("rTRC");
                }
                else if (tagCode == CommandTagXyz)
                {
                    tag = Fourcc("rXYZ");
                }
                else
                {
                    if (tagCode - CommandTagStringFirst >= TagStrings.Length)
                    {
                        throw new JxlDecodingException("Unknown ICC tag code.");
                    }

                    tag = TagStrings[tagCode - CommandTagStringFirst];
                }

                result.AddUInt32(tag);

                ulong tagStart;
                ulong tagSize = previousTagSize;
                if (tag == Fourcc("rXYZ") || tag == Fourcc("gXYZ") || tag == Fourcc("bXYZ") || tag == Fourcc("kXYZ")
                    || tag == Fourcc("wtpt") || tag == Fourcc("bkpt") || tag == Fourcc("lumi"))
                {
                    tagSize = 20;
                }

                if ((command & FlagBitOffset) != 0)
                {
                    tagStart = DecodeVarInt(enc, commandsEnd, ref cpos);
                }
                else
                {
                    CheckIs32Bit(previousTagStart);
                    tagStart = previousTagStart + previousTagSize;
                }

                CheckIs32Bit(tagStart);
                result.AddUInt32((uint)tagStart);
                if ((command & FlagBitSize) != 0)
                {
                    tagSize = DecodeVarInt(enc, commandsEnd, ref cpos);
                }

                CheckIs32Bit(tagSize);
                result.AddUInt32((uint)tagSize);
                previousTagStart = tagStart;
                previousTagSize = tagSize;

                if (tagCode == CommandTagTrc)
                {
                    result.AddUInt32(Fourcc("gTRC"));
                    result.AddUInt32((uint)tagStart);
                    result.AddUInt32((uint)tagSize);
                    result.AddUInt32(Fourcc("bTRC"));
                    result.AddUInt32((uint)tagStart);
                    result.AddUInt32((uint)tagSize);
                }

                if (tagCode == CommandTagXyz)
                {
                    CheckIs32Bit(tagStart + (tagSize * 2));
                    result.AddUInt32(Fourcc("gXYZ"));
                    result.AddUInt32((uint)(tagStart + tagSize));
                    result.AddUInt32((uint)tagSize);
                    result.AddUInt32(Fourcc("bXYZ"));
                    result.AddUInt32((uint)(tagStart + (tagSize * 2)));
                    result.AddUInt32((uint)tagSize);
                }
            }
        }

        // Main content.
        while (true)
        {
            if (result.Count > (int)outputSize || cpos > commandsEnd)
            {
                throw new JxlDecodingException("Invalid ICC result size.");
            }

            if (cpos == commandsEnd)
            {
                break;
            }

            byte command = enc[cpos++];
            if (command == CommandInsert)
            {
                ulong num = DecodeVarInt(enc, commandsEnd, ref cpos);
                CheckBounds((ulong)pos, num, (ulong)size);
                for (ulong i = 0; i < num; i++)
                {
                    result.Add(enc[pos++]);
                }
            }
            else if (command is CommandShuffle2 or CommandShuffle4)
            {
                ulong num = DecodeVarInt(enc, commandsEnd, ref cpos);
                CheckBounds((ulong)pos, num, (ulong)size);
                byte[] shuffled = enc.Slice(pos, (int)num).ToArray();
                Shuffle(shuffled, command == CommandShuffle2 ? 2 : 4);
                foreach (byte b in shuffled)
                {
                    result.Add(b);
                    pos++;
                }
            }
            else if (command == CommandPredict)
            {
                CheckBounds((ulong)cpos, 2, (ulong)commandsEnd);
                byte flags = enc[cpos++];
                int width = (flags & 3) + 1;
                if (width == 3)
                {
                    throw new JxlDecodingException("Invalid ICC predictor width.");
                }

                int order = (flags & 12) >> 2;
                if (order == 3)
                {
                    throw new JxlDecodingException("Invalid ICC predictor order.");
                }

                ulong stride = (ulong)width;
                if ((flags & 16) != 0)
                {
                    stride = DecodeVarInt(enc, commandsEnd, ref cpos);
                    if (stride < (ulong)width)
                    {
                        throw new JxlDecodingException("Invalid ICC predictor stride.");
                    }
                }

                if (result.Count == 0 || (ulong)((result.Count - 1) >> 2) < stride)
                {
                    throw new JxlDecodingException("Invalid ICC predictor stride.");
                }

                ulong num = DecodeVarInt(enc, commandsEnd, ref cpos);
                CheckBounds((ulong)pos, num, (ulong)size);
                byte[] shuffled = enc.Slice(pos, (int)num).ToArray();
                if (width > 1)
                {
                    Shuffle(shuffled, width);
                }

                int start = result.Count;
                for (int i = 0; i < shuffled.Length; i++)
                {
                    byte predicted = LinearPredict(result.Span, start, i, (int)stride, width, order);
                    result.Add((byte)(predicted + shuffled[i]));
                }

                pos += (int)num;
            }
            else if (command == CommandXyz)
            {
                result.AddUInt32(Fourcc("XYZ "));
                result.AddUInt32(0);
                CheckBounds((ulong)pos, 12, (ulong)size);
                for (int i = 0; i < 12; i++)
                {
                    result.Add(enc[pos++]);
                }
            }
            else if (command >= CommandTypeStartFirst && command < CommandTypeStartFirst + TypeStrings.Length)
            {
                result.AddUInt32(TypeStrings[command - CommandTypeStartFirst]);
                result.AddUInt32(0);
            }
            else
            {
                throw new JxlDecodingException("Unknown ICC command.");
            }
        }

        if (pos != size)
        {
            throw new JxlDecodingException("Not all ICC data was used.");
        }

        if (result.Count != (int)outputSize)
        {
            throw new JxlDecodingException("Invalid ICC result size.");
        }

        return result.ToArray();
    }

    private static void PredictHeader(ReadOnlySpan<byte> icc, byte[] header, int pos)
    {
        int size = icc.Length;
        if (pos == 8 && size >= 8)
        {
            header[80] = icc[4];
            header[81] = icc[5];
            header[82] = icc[6];
            header[83] = icc[7];
        }

        if (pos == 41 && size >= 41)
        {
            if (icc[40] == 'A')
            {
                header[41] = (byte)'P';
                header[42] = (byte)'P';
                header[43] = (byte)'L';
            }

            if (icc[40] == 'M')
            {
                header[41] = (byte)'S';
                header[42] = (byte)'F';
                header[43] = (byte)'T';
            }
        }

        if (pos == 42 && size >= 42)
        {
            if (icc[40] == 'S' && icc[41] == 'G')
            {
                header[42] = (byte)'I';
                header[43] = (byte)' ';
            }

            if (icc[40] == 'S' && icc[41] == 'U')
            {
                header[42] = (byte)'N';
                header[43] = (byte)'W';
            }
        }
    }

    // Transposes data viewed as `width` rows by ceil(size / width) columns (and missing trailing cells), in place.
    private static void Shuffle(byte[] data, int width)
    {
        int size = data.Length;
        int height = (size + width - 1) / width;
        var result = new byte[size];
        int s = 0;
        int j = 0;
        for (int i = 0; i < size; i++)
        {
            result[i] = data[j];
            j += height;
            if (j >= size)
            {
                j = ++s;
            }
        }

        result.CopyTo(data, 0);
    }

    private static byte LinearPredict(ReadOnlySpan<byte> data, int start, int i, int stride, int width, int order)
    {
        int pos = start + i;
        if (width == 1)
        {
            byte p1 = data[pos - stride];
            byte p2 = data[pos - (stride * 2)];
            byte p3 = data[pos - (stride * 3)];
            return (byte)Predict(p1, p2, p3, order);
        }

        if (width == 2)
        {
            int p = start + (i & ~1);
            ushort p1 = (ushort)((data[p - stride] << 8) + data[p - stride + 1]);
            ushort p2 = (ushort)((data[p - (stride * 2)] << 8) + data[p - (stride * 2) + 1]);
            ushort p3 = (ushort)((data[p - (stride * 3)] << 8) + data[p - (stride * 3) + 1]);
            ushort predicted = (ushort)Predict(p1, p2, p3, order);
            return (i & 1) != 0 ? (byte)(predicted & 255) : (byte)((predicted >> 8) & 255);
        }

        {
            int p = start + (i & ~3);
            uint p1 = DecodeUInt32(data, pos, p - stride);
            uint p2 = DecodeUInt32(data, pos, p - (stride * 2));
            uint p3 = DecodeUInt32(data, pos, p - (stride * 3));
            uint predicted = unchecked((uint)Predict(p1, p2, p3, order));
            int shiftBytes = 3 - (i & 3);
            return (byte)((predicted >> (shiftBytes * 8)) & 255);
        }
    }

    private static long Predict(long p1, long p2, long p3, int order) => order switch
    {
        0 => p1,
        1 => (2 * p1) - p2,
        2 => (3 * p1) - (3 * p2) + p3,
        _ => 0,
    };

    private static uint DecodeUInt32(ReadOnlySpan<byte> data, int size, int pos) =>
        pos + 4 > size ? 0 : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));

    private static ulong DecodeVarInt(ReadOnlySpan<byte> input, int inputSize, ref int pos)
    {
        ulong result = 0;
        for (int i = 0; i < 9; i++)
        {
            if (pos >= inputSize)
            {
                throw new JxlDecodingException("Truncated ICC varint.");
            }

            byte b = input[pos++];
            result |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        if (pos >= inputSize)
        {
            throw new JxlDecodingException("Truncated ICC varint.");
        }

        byte last = input[pos++];
        if ((last & 0x80) != 0 || (last & 0x7E) != 0)
        {
            throw new JxlDecodingException("ICC varint is out of range.");
        }

        return result | ((ulong)(last & 1) << 63);
    }

    private static void CheckIs32Bit(ulong value)
    {
        if ((value & ~0xFFFFFFFFUL) != 0)
        {
            throw new JxlDecodingException("A 32-bit ICC value was expected.");
        }
    }

    // Checks a + b <= size, including overflow.
    private static void CheckBounds(ulong a, ulong b, ulong size)
    {
        ulong end = a + b;
        if (end > size || end < a)
        {
            throw new JxlDecodingException("ICC data out of bounds.");
        }
    }

    private static uint Fourcc(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    // A fixed-capacity byte list; adding beyond the declared output size is always an error.
    private sealed class IccBuffer
    {
        private readonly byte[] _data;

        public IccBuffer(int capacity) => _data = new byte[capacity];

        public int Count { get; private set; }

        public ReadOnlySpan<byte> Span => _data.AsSpan(0, Count);

        public void Add(byte value)
        {
            if (Count >= _data.Length)
            {
                throw new JxlDecodingException("Invalid ICC result size.");
            }

            _data[Count++] = value;
        }

        public void AddUInt32(uint value)
        {
            if (Count + 4 > _data.Length)
            {
                throw new JxlDecodingException("Invalid ICC result size.");
            }

            BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(Count), value);
            Count += 4;
        }

        public byte[] ToArray() => _data.AsSpan(0, Count).ToArray();
    }
}
