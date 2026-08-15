namespace PeachImage.Formats.Gif.Decoding;

/// <summary>Reorders an interlaced GIF frame's decoded index rows (stored in 4-pass order) into normal top-to-bottom display order.</summary>
internal static class GifInterlacer
{
    public static byte[] Deinterlace(byte[] indices, int width, int height)
    {
        byte[] result = new byte[indices.Length];
        DeinterlaceInto(indices, result, width, height);
        return result;
    }

    /// <summary>Same as <see cref="Deinterlace"/>, but writes into a caller-provided (e.g. array-pool-rented) buffer instead of allocating one.</summary>
    public static void DeinterlaceInto(byte[] indices, byte[] destination, int width, int height)
    {
        int sourceRow = 0;

        sourceRow = CopyPass(indices, destination, width, height, start: 0, step: 8, sourceRow);
        sourceRow = CopyPass(indices, destination, width, height, start: 4, step: 8, sourceRow);
        sourceRow = CopyPass(indices, destination, width, height, start: 2, step: 4, sourceRow);
        CopyPass(indices, destination, width, height, start: 1, step: 2, sourceRow);
    }

    private static int CopyPass(byte[] source, byte[] destination, int width, int height, int start, int step, int sourceRow)
    {
        for (int row = start; row < height; row += step)
        {
            Array.Copy(source, sourceRow * width, destination, row * width, width);
            sourceRow++;
        }

        return sourceRow;
    }
}
