namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// Reads fixed/variable-width (9-12 bit) LZW codes most-significant-bit-first from a byte span — TIFF's
/// bit-packing convention (TIFF 6.0 spec §13), the opposite direction from GIF's least-significant-bit-first
/// packing (see <c>Gif.Decoding.GifLzwBitReader</c>). Uses the same left-aligned 64-bit accumulator shape as
/// <c>Jpeg.Entropy.JpegEntropyReader</c> (extract via <c>buffer &gt;&gt; (64 - bits)</c>, then shift left to
/// discard the consumed bits), which sidesteps the bookkeeping a naive "shift left and OR in from the
/// bottom" accumulator would need to get right to avoid already-consumed bits drifting off the top.
/// </summary>
internal ref struct TiffLzwBitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bytePos;
    private ulong _buffer;
    private int _bitCount;

    /// <summary>Reads the next <paramref name="bits"/>-wide code. Returns <see langword="false"/> once the buffer is exhausted (truncated/malformed data).</summary>
    public bool TryReadCode(int bits, out int code)
    {
        while (_bitCount < bits)
        {
            if (_bytePos >= _data.Length)
            {
                code = 0;
                return false;
            }

            _buffer |= (ulong)_data[_bytePos++] << (56 - _bitCount);
            _bitCount += 8;
        }

        code = (int)(_buffer >> (64 - bits));
        _buffer <<= bits;
        _bitCount -= bits;
        return true;
    }
}
