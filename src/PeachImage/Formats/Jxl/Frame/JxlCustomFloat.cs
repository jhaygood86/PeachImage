using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Formats.Jxl.Frame;

/// <summary>
/// Conversion between the custom floating-point sample formats of JPEG XL (sign bit, <c>exponentBits</c> exponent bits and the
/// remaining mantissa bits, packed into an integer) and binary32.
/// </summary>
internal static class JxlCustomFloat
{
    /// <summary>Converts a packed sample of the given format to binary32.</summary>
    public static float Decode(int pattern, int bits, int exponentBits)
    {
        if (bits == 32)
        {
            return BitConverter.Int32BitsToSingle(pattern);
        }

        uint f = (uint)pattern;
        int exponentBias = (1 << (exponentBits - 1)) - 1;
        int signShift = bits - 1;
        int mantissaBits = bits - exponentBits - 1;
        int mantissaShift = 23 - mantissaBits;
        uint sign = f >> signShift;
        f &= (1u << signShift) - 1;
        if (f == 0)
        {
            return sign != 0 ? -0f : 0f;
        }

        int exponent = (int)(f >> mantissaBits);
        uint mantissa = f & ((1u << mantissaBits) - 1);
        if (exponent == (1 << exponentBits) - 1)
        {
            uint special = (sign != 0 ? 0x80000000u : 0u) | (0xFFu << 23) | (mantissa << mantissaShift);
            return BitConverter.UInt32BitsToSingle(special);
        }

        mantissa <<= mantissaShift;
        if (exponent == 0 && exponentBits < 8)
        {
            // Subnormal: normalize.
            while ((mantissa & 0x800000) == 0)
            {
                mantissa <<= 1;
                exponent--;
            }

            exponent++;
            mantissa &= 0x7FFFFF;
        }

        exponent = exponent - exponentBias + 127;
        if (exponent < 0)
        {
            throw new JxlDecodingException("A custom-float sample is out of range.");
        }

        uint bitsOut = (sign != 0 ? 0x80000000u : 0u) | ((uint)exponent << 23) | mantissa;
        return BitConverter.UInt32BitsToSingle(bitsOut);
    }

    /// <summary>Converts a binary32 value to the packed format (round to nearest even; overflow becomes infinity).</summary>
    public static int Encode(float value, int bits, int exponentBits)
    {
        if (bits == 32)
        {
            return BitConverter.SingleToInt32Bits(value);
        }

        uint f = BitConverter.SingleToUInt32Bits(value);
        uint sign = f >> 31;
        int mantissaBits = bits - exponentBits - 1;
        int exponentBias = (1 << (exponentBits - 1)) - 1;
        uint signBit = sign << (bits - 1);
        uint maxExponent = (1u << exponentBits) - 1;
        int sourceExponent = (int)((f >> 23) & 0xFF);
        uint sourceMantissa = f & 0x7FFFFF;
        if (sourceExponent == 0xFF)
        {
            // Infinity keeps a zero mantissa; NaN keeps a non-zero one.
            uint nanMantissa = sourceMantissa == 0 ? 0 : Math.Max(1u, sourceMantissa >> (23 - mantissaBits));
            return (int)(signBit | (maxExponent << mantissaBits) | nanMantissa);
        }

        if (sourceExponent == 0 && sourceMantissa == 0)
        {
            return (int)signBit;
        }

        int exponent = (sourceExponent == 0 ? 1 : sourceExponent) - 127 + exponentBias;
        int dropped = 23 - mantissaBits;
        ulong full = sourceMantissa | (sourceExponent == 0 ? 0u : 0x800000u);
        if (exponent <= 0)
        {
            // Subnormal in the target: shift the significand (with its implicit leading one) right.
            int extra = 1 - exponent;
            int shift = dropped + extra;
            if (shift > 24)
            {
                return (int)signBit;
            }

            ulong rounded = RoundShift(full, shift);
            return (int)(signBit | (uint)rounded);
        }

        ulong significand = RoundShift(full, dropped);
        // The carry out of the mantissa bumps the exponent (that is the correct rounding behaviour).
        ulong packed = ((ulong)exponent << mantissaBits) + (significand - (1UL << mantissaBits));
        if (packed >= (maxExponent << mantissaBits))
        {
            packed = maxExponent << mantissaBits;
        }

        return (int)(signBit | (uint)packed);
    }

    // value >> shift rounded to nearest, ties to even.
    private static ulong RoundShift(ulong value, int shift)
    {
        if (shift == 0)
        {
            return value;
        }

        ulong q = value >> shift;
        ulong remainder = value & ((1UL << shift) - 1);
        ulong half = 1UL << (shift - 1);
        if (remainder > half || (remainder == half && (q & 1) != 0))
        {
            q++;
        }

        return q;
    }

    /// <summary>The packed-format parameters of a bit depth.</summary>
    public static (int Bits, int ExponentBits) Parameters(JxlBitDepth depth) => ((int)depth.BitsPerSample, (int)depth.ExponentBitsPerSample);
}
