namespace PeachImage.Formats.Gif.Decoding;

/// <summary>
/// Reads fixed-width (2-12 bit) LZW codes least-significant-bit-first from an already-assembled image-data
/// byte buffer. <paramref name="length"/> is the actual amount of valid data in <paramref name="data"/> — it
/// may be array-pool-rented and therefore longer than the real data.
/// </summary>
internal sealed class GifLzwBitReader(byte[] data, int length)
{
    private int _bytePos;
    private ulong _bitBuffer;
    private int _bitCount;

    /// <summary>Reads the next <paramref name="bits"/>-wide code. Returns <see langword="false"/> once the buffer is exhausted (truncated/malformed data).</summary>
    public bool TryReadCode(int bits, out int code)
    {
        if (_bitCount < bits)
        {
            // Bulk refill: one 4-byte read carries enough bits for several subsequent codes (up to 12 bits
            // each), so most calls skip the refill path entirely instead of pulling one byte at a time.
            // _bitCount is at most 11 here (a prior call always drains it below the code width it just
            // consumed), so _bitCount + 32 never approaches the 64-bit buffer's capacity.
            if (length - _bytePos >= 4)
            {
                uint chunk = data[_bytePos] | ((uint)data[_bytePos + 1] << 8) | ((uint)data[_bytePos + 2] << 16) | ((uint)data[_bytePos + 3] << 24);
                _bitBuffer |= (ulong)chunk << _bitCount;
                _bytePos += 4;
                _bitCount += 32;
            }
            else
            {
                while (_bitCount < bits)
                {
                    if (_bytePos >= length)
                    {
                        code = 0;
                        return false;
                    }

                    _bitBuffer |= (ulong)data[_bytePos++] << _bitCount;
                    _bitCount += 8;
                }
            }
        }

        code = (int)(_bitBuffer & ((1UL << bits) - 1));
        _bitBuffer >>= bits;
        _bitCount -= bits;
        return true;
    }
}
