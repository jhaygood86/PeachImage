using System.Numerics;

namespace PeachImage.Formats.Bmp.Decoding;

/// <summary>
/// Converts an arbitrary-width, arbitrary-position bitfield mask (as used by BI_BITFIELDS/BI_ALPHABITFIELDS
/// 16bpp/32bpp pixels) into an 8-bit channel value, uniformly for R/G/B/A — no hardcoded 5-5-5/5-6-5
/// special-casing, so any real-world mask combination works through the same code path.
/// </summary>
internal static class BmpBitfieldMask
{
    /// <summary>A mask's position (trailing zero count) and width (population count).</summary>
    internal readonly record struct MaskInfo(int Shift, int BitCount)
    {
        /// <summary>The largest raw field value this mask can hold: (1 &lt;&lt; BitCount) - 1.</summary>
        public uint MaxValue => BitCount == 0 ? 0 : (1u << BitCount) - 1;
    }

    public static MaskInfo Analyze(uint mask) =>
        mask == 0 ? default : new MaskInfo(BitOperations.TrailingZeroCount(mask), BitOperations.PopCount(mask));

    /// <summary>Extracts and rounds-scales the field selected by <paramref name="mask"/>/<paramref name="info"/> out of <paramref name="packedValue"/> to an 8-bit channel.</summary>
    public static byte Extract(uint packedValue, uint mask, MaskInfo info)
    {
        if (info.BitCount == 0)
        {
            return 0;
        }

        uint raw = (packedValue & mask) >> info.Shift;
        return Scale(raw, info);
    }

    /// <summary>Rounds a raw field value of <see cref="MaskInfo.BitCount"/> bits to an 8-bit channel value via linear scaling.</summary>
    public static byte Scale(uint rawFieldValue, MaskInfo info)
    {
        if (info.BitCount == 0)
        {
            return 0;
        }

        uint maxValue = info.MaxValue;
        return (byte)(((rawFieldValue * 255) + (maxValue / 2)) / maxValue);
    }

    /// <summary>
    /// Determines the R/G/B/A masks a row should actually decode with: the header's own declared masks when
    /// present, otherwise the bit-depth-specific BI_RGB default (X1R5G5B5 for 16bpp, byte-aligned XRGB for
    /// 32bpp).
    /// </summary>
    public static (uint R, uint G, uint B, uint A) ResolveEffectiveMasks(BmpHeader header)
    {
        if (header.RMask != 0 || header.GMask != 0 || header.BMask != 0)
        {
            return (header.RMask, header.GMask, header.BMask, header.AMask);
        }

        return header.BitCount switch
        {
            16 => (0x7C00u, 0x03E0u, 0x001Fu, 0u),
            32 => (0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0u),
            _ => (0u, 0u, 0u, 0u),
        };
    }
}
