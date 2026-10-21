namespace PeachImage.Formats.Jbig2.Decoding;

/// <summary>A most-significant-bit-first reader over JBIG2 data, for headers, Huffman-coded data and raw bitmaps.</summary>
internal ref struct Jbig2BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPosition;

    public Jbig2BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPosition = 0;
    }

    public readonly ReadOnlySpan<byte> Data => _data;

    /// <summary>The index of the byte holding the next unread bit.</summary>
    public readonly int BytePosition => _bitPosition >> 3;

    public int ReadBit()
    {
        int index = _bitPosition >> 3;
        if (index >= _data.Length)
        {
            throw new Jbig2DecodingException("JBIG2 data ended unexpectedly.");
        }

        int bit = (_data[index] >> (7 - (_bitPosition & 7))) & 1;
        _bitPosition++;
        return bit;
    }

    /// <summary>Reads up to 32 bits as an unsigned value.</summary>
    public uint ReadBits(int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            value = (value << 1) | (uint)ReadBit();
        }

        return value;
    }

    public void AlignToByte() => _bitPosition = (_bitPosition + 7) & ~7;

    public void SkipBytes(int count) => _bitPosition += count * 8;
}
