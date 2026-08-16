namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// Plain MSB-first bit reader for AV1's <em>uncompressed</em> header syntax (spec §4.10: <c>f(n)</c>,
/// <c>uvlc()</c>, <c>su(n)</c>, <c>ns(n)</c>). Deliberately a separate type from the multi-symbol
/// arithmetic <c>Av1SymbolDecoder</c> used for everything inside a tile -- unlike VP8, where plain
/// equiprobable bits and coded symbols share one range coder, AV1 uses two genuinely different
/// mechanisms, and conflating them is the easiest correctness mistake to make porting this spec.
/// </summary>
internal sealed class Av1BitReader
{
    private readonly byte[] _data;
    private readonly int _start;
    private readonly int _end;
    private int _bitPosition;

    public Av1BitReader(byte[] data, int start, int length)
    {
        _data = data;
        _start = start;
        _end = start + length;
        _bitPosition = 0;
    }

    /// <summary>The current read position, in bytes from the start of the underlying buffer (rounds down when not byte-aligned).</summary>
    public int BytePosition => _start + (_bitPosition >> 3);

    /// <summary>Total bits consumed so far.</summary>
    public int BitsRead => _bitPosition;

    /// <summary><c>f(n)</c>: reads an <paramref name="n"/>-bit unsigned literal, most-significant bit first.</summary>
    public uint ReadBits(int n)
    {
        uint value = 0;
        for (int i = 0; i < n; i++)
        {
            value = (value << 1) | ReadBit();
        }

        return value;
    }

    /// <summary><c>f(1)</c> read as a boolean.</summary>
    public bool ReadFlag() => ReadBits(1) != 0;

    /// <summary><c>uvlc()</c>: Exp-Golomb-style variable length unsigned code (spec §4.10.3).</summary>
    public uint ReadUvlc()
    {
        int leadingZeroBits = 0;
        while (leadingZeroBits < 32)
        {
            if (ReadBits(1) != 0)
            {
                break;
            }

            leadingZeroBits++;
        }

        if (leadingZeroBits >= 32)
        {
            return uint.MaxValue;
        }

        uint value = ReadBits(leadingZeroBits);
        return value + (1u << leadingZeroBits) - 1;
    }

    /// <summary><c>su(n)</c>: reads <paramref name="n"/> bits as an unsigned value, then reinterprets the top bit as a sign (spec §4.10.6).</summary>
    public int ReadSu(int n)
    {
        int value = (int)ReadBits(n);
        int signMask = 1 << (n - 1);
        if ((value & signMask) != 0)
        {
            value -= 2 * signMask;
        }

        return value;
    }

    /// <summary><c>ns(n)</c>: non-symmetric unsigned encoding for values in <c>[0, n)</c> (spec §4.10.7).</summary>
    public uint ReadNs(uint n)
    {
        if (n <= 1)
        {
            return 0;
        }

        int w = FloorLog2(n) + 1;
        uint m = (uint)((1 << w) - n);
        uint v = ReadBits(w - 1);
        if (v < m)
        {
            return v;
        }

        uint extraBit = ReadBits(1);
        return (v << 1) - m + extraBit;
    }

    /// <summary>Advances to the next byte boundary (spec's <c>byte_alignment()</c>), asserting the skipped bits are all zero padding is <em>not</em> required by this decoder (real encoders are trusted here, matching this codebase's general leniency toward padding/reserved bits).</summary>
    public void ByteAlign()
    {
        _bitPosition = (_bitPosition + 7) & ~7;
    }

    private uint ReadBit()
    {
        int byteIndex = _start + (_bitPosition >> 3);
        if (byteIndex >= _end)
        {
            throw new AvifDecodingException("AV1 bit reader ran past the end of its data.");
        }

        int bitIndexFromMsb = 7 - (_bitPosition & 7);
        uint bit = (uint)((_data[byteIndex] >> bitIndexFromMsb) & 1);
        _bitPosition++;
        return bit;
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
