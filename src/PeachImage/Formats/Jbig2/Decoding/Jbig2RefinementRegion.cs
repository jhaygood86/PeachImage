using PeachImage.Formats.Shared.Arithmetic;

namespace PeachImage.Formats.Jbig2.Decoding;

/// <summary>Generic refinement region decoding (T.88 6.3).</summary>
internal static class Jbig2RefinementRegion
{
    // Fixed template pixels. Context bits are assembled coding pixels first (the adaptive one last), then reference pixels (adaptive last).
    private static readonly sbyte[][] CodingOffsets =
    [
        [0, -1, 1, -1, -1, 0],
        [-1, -1, 0, -1, 1, -1, -1, 0],
    ];

    private static readonly sbyte[][] ReferenceOffsets =
    [
        [0, -1, 1, -1, -1, 0, 0, 0, 1, 0, -1, 1, 0, 1, 1, 1],
        [0, -1, -1, 0, 0, 0, 1, 0, 0, 1, 1, 1],
    ];

    private static readonly int[] SltpContexts = [0x0020, 0x0008];

    /// <summary>The number of context bytes a refinement template uses.</summary>
    public static int ContextSize(int template) => template == 0 ? 1 << 13 : 1 << 10;

    /// <summary>The nominal adaptive pixels of template 0: both at (-1, -1).</summary>
    public static sbyte[] NominalAdaptivePixels() => [-1, -1, -1, -1];

    /// <summary>
    /// Decodes a refinement of <paramref name="reference"/> into a new <paramref name="width"/> x <paramref name="height"/> bitmap.
    /// The reference pixel for (x, y) is at (x - <paramref name="dx"/>, y - <paramref name="dy"/>) in the reference bitmap.
    /// </summary>
    public static Jbig2Bitmap Decode(
        ref MqDecoder decoder,
        Span<byte> contexts,
        int template,
        bool typicalPrediction,
        ReadOnlySpan<sbyte> adaptive,
        int width,
        int height,
        Jbig2Bitmap reference,
        int dx,
        int dy)
    {
        var bitmap = new Jbig2Bitmap(width, height);
        var coding = CodingOffsets[template];
        var refs = ReferenceOffsets[template];
        int codingCount = coding.Length / 2;
        int referenceCount = refs.Length / 2;
        int sltp = SltpContexts[template];
        bool lineIsTypical = false;

        sbyte atCodingX = 0, atCodingY = 0, atRefX = 0, atRefY = 0;
        if (template == 0)
        {
            if (adaptive.Length < 4)
            {
                throw new Jbig2DecodingException("JBIG2 refinement region has too few adaptive template pixels.");
            }

            atCodingX = adaptive[0];
            atCodingY = adaptive[1];
            atRefX = adaptive[2];
            atRefY = adaptive[3];
        }

        for (int y = 0; y < height; y++)
        {
            if (typicalPrediction)
            {
                lineIsTypical ^= decoder.Decode(ref contexts[sltp]) != 0;
            }

            var row = bitmap.Row(y);
            for (int x = 0; x < width; x++)
            {
                int rx = x - dx;
                int ry = y - dy;
                if (lineIsTypical)
                {
                    int center = reference.GetPixel(rx, ry);
                    bool uniform = true;
                    for (int j = -1; j <= 1 && uniform; j++)
                    {
                        for (int i = -1; i <= 1; i++)
                        {
                            if (reference.GetPixel(rx + i, ry + j) != center)
                            {
                                uniform = false;
                                break;
                            }
                        }
                    }

                    if (uniform)
                    {
                        row[x] = (byte)center;
                        continue;
                    }
                }

                int context = 0;
                for (int i = 0; i < codingCount; i++)
                {
                    context = (context << 1) | bitmap.GetPixel(x + coding[i * 2], y + coding[(i * 2) + 1]);
                }

                if (template == 0)
                {
                    context = (context << 1) | bitmap.GetPixel(x + atCodingX, y + atCodingY);
                }

                for (int i = 0; i < referenceCount; i++)
                {
                    context = (context << 1) | reference.GetPixel(rx + refs[i * 2], ry + refs[(i * 2) + 1]);
                }

                if (template == 0)
                {
                    context = (context << 1) | reference.GetPixel(rx + atRefX, ry + atRefY);
                }

                row[x] = (byte)decoder.Decode(ref contexts[context]);
            }
        }

        return bitmap;
    }
}
