using System.Runtime.CompilerServices;
using PeachImage.Formats.Jxl.Internal;

namespace PeachImage.Formats.Jxl.Bitstream;

/// <summary>One of the four selectable distributions of a <c>U32</c> field: a direct value, or extra bits plus an offset.</summary>
internal readonly struct U32Dist
{
    private readonly int _bits;
    private readonly uint _value;

    private U32Dist(int bits, uint value)
    {
        _bits = bits;
        _value = value;
    }

    /// <summary>A distribution that always yields <paramref name="value"/> without reading extra bits.</summary>
    public static U32Dist Val(uint value) => new(0, value);

    /// <summary>A distribution that reads <paramref name="bits"/> extra bits.</summary>
    public static U32Dist Bits(int bits) => new(bits, 0);

    /// <summary>A distribution that reads <paramref name="bits"/> extra bits and adds <paramref name="offset"/>.</summary>
    public static U32Dist BitsOffset(int bits, uint offset) => new(bits, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Read(ref JxlBitReader reader) => _bits == 0 ? _value : reader.ReadBits(_bits) + _value;
}

/// <summary>Readers for the codestream's primitive header field types: U32, U64, Enum, F16 and name strings.</summary>
internal static class JxlFieldReader
{
    public static uint ReadU32(ref JxlBitReader reader, U32Dist d0, U32Dist d1, U32Dist d2, U32Dist d3)
    {
        uint selector = reader.ReadBits(2);
        return selector switch
        {
            0 => d0.Read(ref reader),
            1 => d1.Read(ref reader),
            2 => d2.Read(ref reader),
            _ => d3.Read(ref reader),
        };
    }

    public static ulong ReadU64(ref JxlBitReader reader)
    {
        uint selector = reader.ReadBits(2);
        switch (selector)
        {
            case 0:
                return 0;
            case 1:
                return 1 + reader.ReadBits(4);
            case 2:
                return 17 + reader.ReadBits(8);
        }

        ulong result = reader.ReadBits(12);
        int shift = 12;
        while (reader.ReadBool())
        {
            if (shift == 60)
            {
                result |= (ulong)reader.ReadBits(4) << shift;
                break;
            }

            result |= (ulong)reader.ReadBits(8) << shift;
            shift += 8;
        }

        return result;
    }

    /// <summary>Reads an <c>Enum</c> field: U32(Val(0), Val(1), BitsOffset(4, 2), BitsOffset(6, 18)).</summary>
    public static uint ReadEnum(ref JxlBitReader reader) =>
        ReadU32(ref reader, U32Dist.Val(0), U32Dist.Val(1), U32Dist.BitsOffset(4, 2), U32Dist.BitsOffset(6, 18));

    /// <summary>Reads an <c>F16</c> field: an IEEE binary16 value; infinity and NaN are not permitted.</summary>
    public static float ReadF16(ref JxlBitReader reader)
    {
        uint bits = reader.ReadBits(16);
        if (((bits >> 10) & 0x1F) == 31)
        {
            throw new JxlDecodingException("F16 infinity or NaN is not permitted in a codestream header.");
        }

        return (float)BitConverter.UInt16BitsToHalf((ushort)bits);
    }

    /// <summary>Reads a length-prefixed name string (UTF-8 bytes), as used by extra channels and frames.</summary>
    public static string ReadName(ref JxlBitReader reader)
    {
        uint length = ReadU32(ref reader, U32Dist.Val(0), U32Dist.Bits(4), U32Dist.BitsOffset(5, 16), U32Dist.BitsOffset(10, 48));
        if (length > JxlDecodingLimits.MaxNameLength)
        {
            throw new JxlDecodingException("A name string in the codestream header is too long.");
        }

        if (length == 0)
        {
            return string.Empty;
        }

        Span<byte> bytes = length <= 256 ? stackalloc byte[(int)length] : new byte[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)reader.ReadBits(8);
        }

        reader.ThrowIfOverrun();
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Skips a bundle's trailing extensions field: a 64-bit mask, one U64 bit-size per set bit, then that many
    /// payload bits, which a decoder that does not know the extension ignores.
    /// </summary>
    public static void SkipExtensions(ref JxlBitReader reader)
    {
        ulong mask = ReadU64(ref reader);
        if (mask == 0)
        {
            return;
        }

        ulong total = 0;
        for (ulong remaining = mask; remaining != 0; remaining &= remaining - 1)
        {
            ulong bits = ReadU64(ref reader);
            total += bits;
            if (total < bits || total > long.MaxValue)
            {
                throw new JxlDecodingException("Invalid extension size.");
            }
        }

        reader.Skip((long)total);
    }

    /// <summary>Unpacks a zigzag-style signed value: even → v/2, odd → -(v+1)/2.</summary>
    public static int UnpackSigned(uint value) => (int)(value >> 1) ^ -(int)(value & 1);
}
