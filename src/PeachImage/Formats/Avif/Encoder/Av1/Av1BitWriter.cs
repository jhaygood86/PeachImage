namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Plain MSB-first bit writer for AV1's <em>uncompressed</em> header syntax (spec §4.10: <c>f(n)</c>,
/// <c>uvlc()</c>, <c>su(n)</c>, <c>ns(n)</c>). The write-side mirror of
/// <see cref="Decoding.Av1.Av1BitReader"/> -- see its remarks for why this stays a separate type from
/// the multi-symbol arithmetic <c>Av1SymbolEncoder</c> (added alongside the entropy-coding layer) used inside tiles.
/// </summary>
internal sealed class Av1BitWriter
{
    private byte[] _buffer;
    private int _bitPosition;

    public Av1BitWriter(int initialByteCapacity = 64)
    {
        _buffer = new byte[Math.Max(1, initialByteCapacity)];
        _bitPosition = 0;
    }

    /// <summary>Total bits written so far.</summary>
    public int BitsWritten => _bitPosition;

    /// <summary><c>f(n)</c>: writes <paramref name="value"/> as an <paramref name="n"/>-bit unsigned literal, most-significant bit first.</summary>
    public void WriteBits(uint value, int n)
    {
        for (int i = n - 1; i >= 0; i--)
        {
            WriteBit((value >> i) & 1);
        }
    }

    /// <summary><c>f(1)</c> written from a boolean.</summary>
    public void WriteFlag(bool value) => WriteBits(value ? 1u : 0u, 1);

    /// <summary><c>uvlc()</c>: Exp-Golomb-style variable length unsigned code (spec §4.10.3), inverse of <see cref="Decoding.Av1.Av1BitReader.ReadUvlc"/>.</summary>
    public void WriteUvlc(uint value)
    {
        uint valuePlus1 = value + 1;
        int leadingZeroBits = FloorLog2(valuePlus1);

        for (int i = 0; i < leadingZeroBits; i++)
        {
            WriteBit(0);
        }

        WriteBit(1);

        if (leadingZeroBits > 0)
        {
            WriteBits(valuePlus1 - (1u << leadingZeroBits), leadingZeroBits);
        }
    }

    /// <summary><c>su(n)</c>: writes a signed value using <paramref name="n"/> bits, top bit as sign (spec §4.10.6), inverse of <see cref="Decoding.Av1.Av1BitReader.ReadSu"/>.</summary>
    public void WriteSu(int value, int n)
    {
        int signMask = 1 << (n - 1);
        uint raw = value < 0 ? (uint)(value + (2 * signMask)) : (uint)value;
        WriteBits(raw, n);
    }

    /// <summary><c>ns(n)</c>: non-symmetric unsigned encoding for values in <c>[0, n)</c> (spec §4.10.7), inverse of <see cref="Decoding.Av1.Av1BitReader.ReadNs"/>.</summary>
    public void WriteNs(uint value, uint n)
    {
        if (n <= 1)
        {
            return;
        }

        int w = FloorLog2(n) + 1;
        uint m = (uint)((1 << w) - n);

        if (value < m)
        {
            WriteBits(value, w - 1);
            return;
        }

        uint s = value + m;
        WriteBits(s >> 1, w - 1);
        WriteBit(s & 1);
    }

    /// <summary>Advances to the next byte boundary (spec's <c>byte_alignment()</c>), padding with zero bits.</summary>
    public void ByteAlign()
    {
        while ((_bitPosition & 7) != 0)
        {
            WriteBit(0);
        }
    }

    /// <summary>
    /// <c>trailing_bits()</c> (spec §5.3.4): writes a single "1" stop bit, then zero-pads to the next byte
    /// boundary -- unlike <see cref="ByteAlign"/>, this always writes at least one bit, even when already
    /// byte-aligned (in which case it writes a full extra 0x80 byte). Required at the end of
    /// <c>sequence_header_obu()</c> specifically: omitting it is harmless when the header's natural content
    /// leaves spare padding bits in the final byte (a lenient reader just sees trailing zeros either way),
    /// but when the content lands exactly on a byte boundary there is no spare byte left at all -- a strict
    /// decoder (dav1d included) trying to read the mandatory stop bit then reads past the OBU's declared
    /// length and reports a buffer overrun, even though the payload bytes it did receive were otherwise
    /// perfectly valid.
    /// </summary>
    public void WriteTrailingBits()
    {
        WriteBit(1);
        while ((_bitPosition & 7) != 0)
        {
            WriteBit(0);
        }
    }

    /// <summary>Returns the written bits as a byte array, rounded up to the nearest byte (any padding bits in the final byte are zero).</summary>
    public byte[] ToArray()
    {
        int byteLength = (_bitPosition + 7) >> 3;
        var result = new byte[byteLength];
        Array.Copy(_buffer, result, byteLength);
        return result;
    }

    private void WriteBit(uint bit)
    {
        int byteIndex = _bitPosition >> 3;
        if (byteIndex >= _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        if (bit != 0)
        {
            int bitIndexFromMsb = 7 - (_bitPosition & 7);
            _buffer[byteIndex] |= (byte)(1 << bitIndexFromMsb);
        }

        _bitPosition++;
    }

    private static int FloorLog2(uint x)
    {
        int s = 0;
        while (x != 0)
        {
            x >>= 1;
            s++;
        }

        return s - 1;
    }
}
