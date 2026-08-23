namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// Resolves a TIFF ColorMap — three consecutive 16-bit-scale arrays of length <c>1 &lt;&lt; bitsPerSample</c>
/// (R, then G, then B; see TIFF 6.0 §5's ColorMap layout, a different shape from BMP's interleaved BGR[A]
/// palette entries) — into a flat <c>byte[entryCount * 3]</c> RGB24 table, scaling each 16-bit channel value
/// down to 8-bit by top-byte truncation (matches this codebase's existing 16-&gt;8-bit narrowing convention,
/// e.g. <c>PixelFormatConversionKernels.NarrowUInt16ToBytes</c>).
/// </summary>
internal static class TiffPalette
{
    public static byte[] Resolve(uint[] colorMap, int bitsPerSample)
    {
        int entryCount = 1 << bitsPerSample;
        var rgb = new byte[entryCount * 3];

        for (int i = 0; i < entryCount; i++)
        {
            rgb[(i * 3) + 0] = (byte)(colorMap[i] >> 8);
            rgb[(i * 3) + 1] = (byte)(colorMap[entryCount + i] >> 8);
            rgb[(i * 3) + 2] = (byte)(colorMap[(entryCount * 2) + i] >> 8);
        }

        return rgb;
    }
}
