namespace PeachImage.Formats.Gif.Encoding;

/// <summary>Nearest-color lookup against a flat RGB-triple palette (linear scan — palettes are at most 256 entries, so this is cheap).</summary>
internal static class GifPaletteLookup
{
    public static int NearestIndex(byte r, byte g, byte b, byte[] palette)
    {
        int entryCount = palette.Length / 3;
        int bestIndex = 0;
        int bestDistance = int.MaxValue;

        for (int i = 0; i < entryCount; i++)
        {
            int offset = i * 3;
            int dr = r - palette[offset];
            int dg = g - palette[offset + 1];
            int db = b - palette[offset + 2];
            int distance = (dr * dr) + (dg * dg) + (db * db);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
                if (distance == 0)
                {
                    break;
                }
            }
        }

        return bestIndex;
    }
}
