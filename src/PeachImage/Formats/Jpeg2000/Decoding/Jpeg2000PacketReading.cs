namespace PeachImage.Formats.Jpeg2000.Decoding;

/// <summary>A tag tree (T.800 B.10.2) as used for code-block inclusion and zero bit-plane counts in packet headers.</summary>
internal sealed class J2kTagTree
{
    private readonly int[] _value;
    private readonly int[] _low;
    private readonly int[] _levelOffset;
    private readonly int[] _levelWidth;
    private readonly int _levels;

    public J2kTagTree(int width, int height)
    {
        var offsets = new List<int>();
        var widths = new List<int>();
        int total = 0;
        int w = width;
        int h = height;
        while (true)
        {
            offsets.Add(total);
            widths.Add(w);
            total += w * h;
            if (w <= 1 && h <= 1)
            {
                break;
            }

            w = (w + 1) >> 1;
            h = (h + 1) >> 1;
        }

        _levels = offsets.Count;
        _levelOffset = [.. offsets];
        _levelWidth = [.. widths];
        _value = new int[total];
        _low = new int[total];
        Array.Fill(_value, int.MaxValue);
    }

    /// <summary>Reads enough of the tree to tell whether the leaf's value is below <paramref name="threshold"/>.</summary>
    public bool Decode(ref J2kPacketBitReader reader, int x, int y, int threshold)
    {
        Span<int> path = stackalloc int[40];
        int count = 0;
        for (int level = 0; level < _levels; level++)
        {
            path[count++] = _levelOffset[level] + ((y >> level) * _levelWidth[level]) + (x >> level);
        }

        int low = 0;
        for (int i = count - 1; i >= 0; i--)
        {
            int node = path[i];
            if (low > _low[node])
            {
                _low[node] = low;
            }
            else
            {
                low = _low[node];
            }

            while (low < threshold && low < _value[node])
            {
                if (reader.ReadBit() != 0)
                {
                    _value[node] = low;
                }
                else
                {
                    low++;
                }
            }

            _low[node] = low;
        }

        return _value[path[0]] < threshold;
    }
}

/// <summary>Reads packet-header bits (T.800 B.10.1): most significant bit first, and the byte after 0xFF carries only seven bits.</summary>
internal ref struct J2kPacketBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;
    private int _buffer;
    private int _count;

    public J2kPacketBitReader(ReadOnlySpan<byte> data, int position)
    {
        _data = data;
        _position = position;
        _buffer = 0;
        _count = 0;
    }

    /// <summary>The offset of the first byte not yet consumed.</summary>
    public readonly int Position => _position;

    public int ReadBit()
    {
        if (_count == 0)
        {
            _buffer = (_buffer << 8) & 0xFFFF;
            _count = _buffer == 0xFF00 ? 7 : 8;
            if (_position >= _data.Length)
            {
                throw new J2kTruncatedException();
            }

            _buffer |= _data[_position++];
        }

        _count--;
        return (_buffer >> _count) & 1;
    }

    public int ReadBits(int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            value = (value << 1) | ReadBit();
        }

        return value;
    }

    /// <summary>Ends the header at a byte boundary, consuming the stuffed byte that follows a trailing 0xFF.</summary>
    public void Align()
    {
        if ((_buffer & 0xFF) == 0xFF)
        {
            if (_position >= _data.Length)
            {
                throw new J2kTruncatedException();
            }

            _position++;
        }

        _count = 0;
        _buffer = 0;
    }
}

/// <summary>Raised when packet data ends before the packets it should hold; the tile is then decoded from what was read.</summary>
internal sealed class J2kTruncatedException : Exception
{
    public J2kTruncatedException()
        : base("JPEG 2000 packet data ends unexpectedly.")
    {
    }
}
