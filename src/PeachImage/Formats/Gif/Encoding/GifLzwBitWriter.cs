namespace PeachImage.Formats.Gif.Encoding;

/// <summary>Packs fixed-width (2-12 bit) LZW codes least-significant-bit-first and frames the resulting bytes into GIF's 255-byte-max sub-blocks.</summary>
internal sealed class GifLzwBitWriter(Stream stream)
{
    private readonly byte[] _subBlock = new byte[255];
    private int _subBlockLength;
    private int _bitBuffer;
    private int _bitCount;

    public void WriteCode(int code, int bits)
    {
        _bitBuffer |= code << _bitCount;
        _bitCount += bits;

        while (_bitCount >= 8)
        {
            EmitByte((byte)(_bitBuffer & 0xFF));
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }
    }

    /// <summary>Flushes any partial byte and any partial sub-block, then writes the zero-length terminator sub-block.</summary>
    public void Finish()
    {
        if (_bitCount > 0)
        {
            EmitByte((byte)(_bitBuffer & 0xFF));
            _bitBuffer = 0;
            _bitCount = 0;
        }

        FlushSubBlock();
        stream.WriteByte(0);
    }

    private void EmitByte(byte value)
    {
        _subBlock[_subBlockLength++] = value;
        if (_subBlockLength == _subBlock.Length)
        {
            FlushSubBlock();
        }
    }

    private void FlushSubBlock()
    {
        if (_subBlockLength == 0)
        {
            return;
        }

        stream.WriteByte((byte)_subBlockLength);
        stream.Write(_subBlock, 0, _subBlockLength);
        _subBlockLength = 0;
    }
}
