namespace PeachImage.Formats.Jpeg.Entropy;

/// <summary>
/// Bit-level writer for a JPEG entropy-coded segment. Stuffs a <c>0x00</c> byte after every literal
/// <c>0xFF</c> data byte, mirroring <see cref="JpegEntropyReader"/>'s destuffing.
/// </summary>
internal sealed class JpegEntropyWriter(Stream stream)
{
    private uint _buffer;
    private int _bitCount;

    /// <summary>Writes the low <paramref name="bitCount"/> bits of <paramref name="value"/>, most-significant bit first.</summary>
    public void WriteBits(int value, int bitCount)
    {
        if (bitCount == 0)
        {
            return;
        }

        uint masked = (uint)value & ((1u << bitCount) - 1);
        _buffer |= masked << (32 - _bitCount - bitCount);
        _bitCount += bitCount;

        while (_bitCount >= 8)
        {
            byte b = (byte)(_buffer >> 24);
            WriteStuffedByte(b);
            _buffer <<= 8;
            _bitCount -= 8;
        }
    }

    /// <summary>Pads any partial byte with 1-bits and flushes it, leaving the writer byte-aligned.</summary>
    public void Flush()
    {
        if (_bitCount > 0)
        {
            int padBits = 8 - _bitCount;
            WriteBits((1 << padBits) - 1, padBits);
        }
    }

    /// <summary>Resets bit-accumulation state, e.g. after writing a restart marker.</summary>
    public void Reset()
    {
        _buffer = 0;
        _bitCount = 0;
    }

    private void WriteStuffedByte(byte b)
    {
        stream.WriteByte(b);
        if (b == 0xFF)
        {
            stream.WriteByte(0x00);
        }
    }
}
