namespace PeachImage.Formats.Jpeg.Encoding;

/// <summary>
/// Builds quantization tables from a 1-100 quality parameter using the IJG quality-scaling formula applied
/// to the standard Annex K.1 base tables.
/// </summary>
internal static class QuantizationTableFactory
{
    private static readonly ushort[] StandardLuminance =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly ushort[] StandardChrominance =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    /// <summary>Builds a luminance quantization table (natural order) for the given quality (1-100).</summary>
    public static ushort[] CreateLuminance(int quality) => Scale(StandardLuminance, quality);

    /// <summary>Builds a chrominance quantization table (natural order) for the given quality (1-100).</summary>
    public static ushort[] CreateChrominance(int quality) => Scale(StandardChrominance, quality);

    private static ushort[] Scale(ushort[] baseTable, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        int scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);

        var result = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            int value = ((baseTable[i] * scale) + 50) / 100;
            result[i] = (ushort)Math.Clamp(value, 1, 255);
        }

        return result;
    }
}
