using System.Numerics;
using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Entropy;

namespace PeachImage.Formats.Jxl.Encoder.Entropy;

/// <summary>The write side of <see cref="HybridUintConfig"/>: splits a value into an entropy-coded token plus raw extra bits.</summary>
internal static class JxlHybridUintEncoder
{
    /// <summary>The configuration libjxl uses for most contexts: values below 16 are tokens, larger ones keep two mantissa bits in the token.</summary>
    public static HybridUintConfig Default { get; } = new(4, 2, 0);

    /// <summary>
    /// Splits <paramref name="value"/> into a token and <paramref name="numExtraBits"/> raw bits
    /// (<paramref name="extraBits"/>); the inverse of <see cref="HybridUintConfig.Decode"/>.
    /// </summary>
    public static uint Split(in HybridUintConfig config, uint value, out uint extraBits, out int numExtraBits)
    {
        if (value < config.SplitToken)
        {
            extraBits = 0;
            numExtraBits = 0;
            return value;
        }

        int n = 31 - BitOperations.LeadingZeroCount(value);
        int msb = config.MsbInToken;
        int lsb = config.LsbInToken;
        uint mantissa = value - (1u << n);
        numExtraBits = n - msb - lsb;
        extraBits = (value >> lsb) & ((1u << numExtraBits) - 1);
        uint hi = mantissa >> (n - msb);
        uint low = value & ((1u << lsb) - 1);
        return config.SplitToken + (((uint)(n - config.SplitExponent)) << (msb + lsb)) + (hi << lsb) + low;
    }

    /// <summary>The largest token <see cref="Split"/> can produce for a 32-bit value, plus one.</summary>
    public static int AlphabetSize(in HybridUintConfig config) =>
        (int)Split(config, uint.MaxValue, out _, out _) + 1;

    /// <summary>Writes <paramref name="config"/> for an alphabet of <c>1 &lt;&lt; logAlphaSize</c> symbols; the inverse of <see cref="HybridUintConfig.Read"/>.</summary>
    public static void WriteConfig(JxlBitWriter writer, in HybridUintConfig config, int logAlphaSize)
    {
        writer.WriteBits((uint)config.SplitExponent, CeilLog2(logAlphaSize + 1));
        if (config.SplitExponent != logAlphaSize)
        {
            writer.WriteBits((uint)config.MsbInToken, CeilLog2(config.SplitExponent + 1));
            writer.WriteBits((uint)config.LsbInToken, CeilLog2(config.SplitExponent - config.MsbInToken + 1));
        }
    }

    private static int CeilLog2(int x) => x <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(x - 1));
}
