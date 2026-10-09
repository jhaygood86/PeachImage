using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace PeachImage.Formats.Jxl.Bitstream;

/// <summary>
/// An LSB-first bit reader over a JPEG XL codestream. Reading past the end never throws from the read
/// itself: it yields zero bits and latches <see cref="IsOverrun"/>, so tight loops stay branch-light; callers
/// call <see cref="ThrowIfOverrun"/> at the boundaries where truncation must be reported.
/// </summary>
internal ref struct JxlBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private long _bitPosition;
    private bool _overrun;

    public JxlBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPosition = 0;
        _overrun = false;
    }

    /// <summary>Creates a reader positioned <paramref name="bitPosition"/> bits into <paramref name="data"/>.</summary>
    public JxlBitReader(ReadOnlySpan<byte> data, long bitPosition)
    {
        _data = data;
        _bitPosition = bitPosition;
        _overrun = bitPosition > (long)data.Length * 8;
    }

    /// <summary>The number of bits consumed so far (including any consumed past the end of the data).</summary>
    public readonly long BitPosition => _bitPosition;

    /// <summary>Whether any read has gone past the end of the data.</summary>
    public readonly bool IsOverrun => _overrun;

    /// <summary>The number of bits left before the end of the data (zero once overrun).</summary>
    public readonly long BitsRemaining => _overrun ? 0 : ((long)_data.Length * 8) - _bitPosition;

    /// <summary>Reads <paramref name="count"/> (0 to 32) bits, least-significant first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        ulong window = Peek64();
        _bitPosition += count;
        if (_bitPosition > (long)_data.Length * 8)
        {
            _overrun = true;
        }

        return (uint)(window & ((1UL << count) - 1));
    }

    /// <summary>Returns the next <paramref name="count"/> (0 to 32) bits without consuming them; bits past the end read as zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint PeekBits(int count) => (uint)(Peek64() & ((1UL << count) - 1));

    /// <summary>Consumes <paramref name="count"/> bits previously examined with <see cref="PeekBits"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Consume(int count)
    {
        _bitPosition += count;
        if (_bitPosition > (long)_data.Length * 8)
        {
            _overrun = true;
        }
    }

    /// <summary>Reads a single bit as a bool.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool() => ReadBits(1) != 0;

    /// <summary>Reads up to 64 bits.</summary>
    public ulong ReadBits64(int count)
    {
        if (count <= 32)
        {
            return ReadBits(count);
        }

        ulong low = ReadBits(32);
        ulong high = ReadBits(count - 32);
        return low | (high << 32);
    }

    /// <summary>Skips <paramref name="count"/> bits.</summary>
    public void Skip(long count)
    {
        _bitPosition += count;
        if (_bitPosition > (long)_data.Length * 8)
        {
            _overrun = true;
        }
    }

    /// <summary>Advances to the next byte boundary, requiring the skipped padding bits to be zero as the format mandates.</summary>
    public void ZeroPadToByte()
    {
        int pad = (int)((8 - (_bitPosition & 7)) & 7);
        if (pad != 0 && ReadBits(pad) != 0)
        {
            throw new JxlDecodingException("Non-zero padding bits before a byte boundary.");
        }
    }

    /// <summary>Throws <see cref="JxlDecodingException"/> if any read went past the end of the data.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void ThrowIfOverrun()
    {
        if (_overrun)
        {
            ThrowTruncated();
        }
    }

    // Returns 64 bits starting at the current position; bits beyond the data are zero. At least 57 are valid.
    private readonly ulong Peek64()
    {
        long byteIndex = _bitPosition >> 3;
        int shift = (int)(_bitPosition & 7);
        if (byteIndex + 8 <= _data.Length)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice((int)byteIndex, 8)) >> shift;
        }

        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            long index = byteIndex + i;
            if (index >= 0 && index < _data.Length)
            {
                value |= (ulong)_data[(int)index] << (8 * i);
            }
        }

        return value >> shift;
    }

    private static void ThrowTruncated() => throw new JxlDecodingException("The codestream ended unexpectedly.");
}
