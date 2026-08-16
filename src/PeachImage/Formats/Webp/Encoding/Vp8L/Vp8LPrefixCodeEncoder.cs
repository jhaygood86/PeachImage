using System.Numerics;

namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>
/// Inverts <see cref="Decoding.Vp8L.Vp8LBackwardReferenceTables.DecodePrefixCodeValue"/>: given a real
/// length or (pre-mapping) distance "plane code" value, produces the prefix-code symbol and extra bits a
/// decoder would need to read to reconstruct it. Serves both the green alphabet's length symbols (256-279)
/// and the distance alphabet's plane codes, exactly as the decode-side function serves both directions.
/// </summary>
internal static class Vp8LPrefixCodeEncoder
{
    /// <summary>Encodes <paramref name="value"/> (&gt;= 1) as <c>(symbol, extraValue, extraBits)</c>. Symbols 0-3 need no extra bits; higher symbols need a doubling range of extra bits.</summary>
    public static (int Symbol, uint ExtraValue, int ExtraBits) EncodePrefixCodeValue(int value)
    {
        if (value <= 4)
        {
            return (value - 1, 0, 0);
        }

        int n = value - 1;
        int extraBits = BitOperations.Log2((uint)n) - 1;
        int offsetUnit = n >> extraBits;
        int symbol = (2 * extraBits) + offsetUnit;
        uint extraValue = (uint)(n - (offsetUnit << extraBits));

        return (symbol, extraValue, extraBits);
    }
}
