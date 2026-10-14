using PeachImage.Formats.Jxl.Bitstream;

namespace PeachImage.Formats.Jxl.Encoder.Bitstream;

/// <summary>
/// An LSB-first bit writer, the inverse of <see cref="JxlBitReader"/>: the first bit written lands in the least
/// significant bit of the first byte. Also carries the header-field writers (U32, U64, Enum, F16, names), which
/// mirror <see cref="JxlFieldReader"/>.
/// </summary>
internal sealed class JxlBitWriter
{
    private byte[] _buffer;
    private int _length;
    private ulong _accumulator;
    private int _accumulatedBits;

    public JxlBitWriter(int initialCapacity = 256)
    {
        _buffer = new byte[Math.Max(16, initialCapacity)];
    }

    /// <summary>The number of bits written so far.</summary>
    public long BitPosition => ((long)_length << 3) + _accumulatedBits;

    /// <summary>Whether the write position is on a byte boundary.</summary>
    public bool IsByteAligned => _accumulatedBits == 0;

    /// <summary>Writes the low <paramref name="count"/> bits (0 to 56) of <paramref name="value"/>.</summary>
    public void WriteBits(ulong value, int count)
    {
        if (count == 0)
        {
            return;
        }

        if (count > 56)
        {
            // Split so the accumulator (at most 7 pending bits) never overflows 64 bits.
            WriteBits(value & 0xFFFFFFFFUL, 32);
            WriteBits(value >> 32, count - 32);
            return;
        }

        _accumulator |= (value & ((1UL << count) - 1)) << _accumulatedBits;
        _accumulatedBits += count;
        while (_accumulatedBits >= 8)
        {
            Append((byte)_accumulator);
            _accumulator >>= 8;
            _accumulatedBits -= 8;
        }
    }

    public void WriteBool(bool value) => WriteBits(value ? 1UL : 0UL, 1);

    /// <summary>Pads with zero bits up to the next byte boundary.</summary>
    public void ZeroPadToByte()
    {
        if (_accumulatedBits != 0)
        {
            WriteBits(0, 8 - _accumulatedBits);
        }
    }

    /// <summary>Appends whole bytes; the writer must be byte-aligned.</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (_accumulatedBits != 0)
        {
            throw new InvalidOperationException("WriteBytes requires a byte-aligned position.");
        }

        Reserve(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    /// <summary>Writes a canonical prefix code MSB-first, the order a prefix-code decoder consumes bits.</summary>
    public void WriteCodeMsbFirst(uint code, int length)
    {
        for (int i = length - 1; i >= 0; i--)
        {
            WriteBits((code >> i) & 1, 1);
        }
    }

    /// <summary>Writes a <c>U32</c> field using whichever of the four distributions represents it in the fewest bits.</summary>
    public void WriteU32(uint value, U32Dist d0, U32Dist d1, U32Dist d2, U32Dist d3)
    {
        Span<U32Dist> dists = [d0, d1, d2, d3];
        int best = -1;
        uint bestRaw = 0;
        for (int i = 0; i < dists.Length; i++)
        {
            if (dists[i].TryEncode(value, out uint raw) && (best < 0 || dists[i].ExtraBits < dists[best].ExtraBits))
            {
                best = i;
                bestRaw = raw;
            }
        }

        if (best < 0)
        {
            throw new JxlEncodingException($"The value {value} cannot be represented by the U32 field.");
        }

        WriteBits((uint)best, 2);
        WriteBits(bestRaw, dists[best].ExtraBits);
    }

    public void WriteU64(ulong value)
    {
        if (value == 0)
        {
            WriteBits(0, 2);
            return;
        }

        if (value <= 16)
        {
            WriteBits(1, 2);
            WriteBits(value - 1, 4);
            return;
        }

        if (value <= 272)
        {
            WriteBits(2, 2);
            WriteBits(value - 17, 8);
            return;
        }

        WriteBits(3, 2);
        WriteBits(value & 0xFFF, 12);
        ulong rest = value >> 12;
        int shift = 12;
        while (true)
        {
            if (rest == 0)
            {
                WriteBool(false);
                return;
            }

            WriteBool(true);
            if (shift == 60)
            {
                WriteBits(rest, 4);
                return;
            }

            WriteBits(rest & 0xFF, 8);
            rest >>= 8;
            shift += 8;
        }
    }

    /// <summary>Writes an <c>Enum</c> field: U32(Val(0), Val(1), BitsOffset(4, 2), BitsOffset(6, 18)).</summary>
    public void WriteEnum(uint value) =>
        WriteU32(value, U32Dist.Val(0), U32Dist.Val(1), U32Dist.BitsOffset(4, 2), U32Dist.BitsOffset(6, 18));

    /// <summary>Writes an <c>F16</c> field (IEEE binary16).</summary>
    public void WriteF16(float value)
    {
        var half = (Half)value;
        if (!Half.IsFinite(half))
        {
            throw new JxlEncodingException("F16 infinity or NaN is not permitted in a codestream header.");
        }

        WriteBits(BitConverter.HalfToUInt16Bits(half), 16);
    }

    /// <summary>Writes a length-prefixed UTF-8 name string.</summary>
    public void WriteName(string name)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
        WriteU32((uint)bytes.Length, U32Dist.Val(0), U32Dist.Bits(4), U32Dist.BitsOffset(5, 16), U32Dist.BitsOffset(10, 48));
        foreach (byte b in bytes)
        {
            WriteBits(b, 8);
        }
    }

    /// <summary>Zero-pads to a byte boundary and returns the written bytes.</summary>
    public byte[] ToArray()
    {
        ZeroPadToByte();
        return _buffer.AsSpan(0, _length).ToArray();
    }

    /// <summary>Zero-pads to a byte boundary and returns a view of the written bytes (valid until the next write).</summary>
    public ReadOnlySpan<byte> AsSpan()
    {
        ZeroPadToByte();
        return _buffer.AsSpan(0, _length);
    }

    private void Append(byte value)
    {
        if (_length == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        _buffer[_length++] = value;
    }

    private void Reserve(int extra)
    {
        long needed = (long)_length + extra;
        if (needed > _buffer.Length)
        {
            long size = _buffer.Length;
            while (size < needed)
            {
                size *= 2;
            }

            Array.Resize(ref _buffer, checked((int)size));
        }
    }
}
