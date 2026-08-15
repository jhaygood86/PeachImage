namespace PeachImage.Formats.Gif.Decoding;

/// <summary>Reads a GIF Global/Local Color Table (a flat run of RGB triples) into a flat <c>byte[entryCount * 3]</c> array.</summary>
internal static class GifColorTable
{
    public static byte[] Read(Stream stream, int entryCount)
    {
        byte[] table = new byte[entryCount * 3];
        GifStreamHelpers.ReadExactlyOrThrow(stream, table);
        return table;
    }
}
