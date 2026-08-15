namespace PeachImage.Formats.Gif.Decoding;

/// <summary>
/// Reads fixed-width (2-12 bit) LZW codes least-significant-bit-first from an already-assembled image-data
/// byte buffer. <paramref name="length"/> is the actual amount of valid data in <paramref name="data"/> — it
/// may be array-pool-rented and therefore longer than the real data.
/// </summary>
internal sealed class GifLzwBitReader(byte[] data, int length)
{
    private int _bytePos;
    private int _bitBuffer;
    private int _bitCount;

    /// <summary>Reads the next <paramref name="bits"/>-wide code. Returns <see langword="false"/> once the buffer is exhausted (truncated/malformed data).</summary>
    public bool TryReadCode(int bits, out int code)
    {
        while (_bitCount < bits)
        {
            if (_bytePos >= length)
            {
                code = 0;
                return false;
            }

            _bitBuffer |= data[_bytePos++] << _bitCount;
            _bitCount += 8;
        }

        code = _bitBuffer & ((1 << bits) - 1);
        _bitBuffer >>= bits;
        _bitCount -= bits;
        return true;
    }
}
