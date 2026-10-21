using PeachImage.Formats.Shared.Arithmetic;
using PeachImage.Formats.Shared.Ccitt;

namespace PeachImage.Formats.Jbig2.Decoding;

/// <summary>Generic region decoding (T.88 6.2): arithmetic-coded with the four templates, or MMR.</summary>
internal static class Jbig2GenericRegion
{
    private static readonly int[] ContextBits = [16, 13, 10, 10];
    private static readonly int[] SltpContexts = [0x9B25, 0x0795, 0x00E5, 0x0195];

    // Per template: number of pixels in the window of row y-2, y-1 and y (the pixels to the left of the current one).
    private static readonly int[] Row2Size = [5, 4, 3, 0];
    private static readonly int[] Row1Size = [7, 6, 5, 6];
    private static readonly int[] Row0Size = [4, 3, 2, 4];

    // Extent of the windows to the right of the current pixel.
    private static readonly int[] Row2Right = [2, 2, 1, 0];
    private static readonly int[] Row1Right = [3, 3, 2, 2];

    // Adaptive-template pixels: the context bit each occupies, and its nominal position.
    private static readonly int[][] AdaptiveBits = [[4, 10, 11, 15], [3], [2], [4]];
    private static readonly int[][] NominalX = [[3, -3, 2, -2], [3], [2], [2]];
    private static readonly int[][] NominalY = [[-1, -1, -2, -2], [-1], [-1], [-1]];

    /// <summary>The nominal adaptive-template pixel offsets (x0, y0, x1, y1, ...) for a template.</summary>
    public static sbyte[] NominalAdaptivePixels(int template)
    {
        var result = new sbyte[NominalX[template].Length * 2];
        for (int i = 0; i < NominalX[template].Length; i++)
        {
            result[i * 2] = (sbyte)NominalX[template][i];
            result[(i * 2) + 1] = (sbyte)NominalY[template][i];
        }

        return result;
    }

    /// <summary>The number of context bytes a template uses.</summary>
    public static int ContextSize(int template) => 1 << ContextBits[template];

    /// <summary>Decodes an arithmetic-coded generic region into a new bitmap.</summary>
    public static Jbig2Bitmap DecodeArithmetic(
        ref MqDecoder decoder,
        Span<byte> contexts,
        int template,
        bool typicalPrediction,
        ReadOnlySpan<sbyte> adaptive,
        int width,
        int height,
        Jbig2Bitmap? skip)
    {
        var bitmap = new Jbig2Bitmap(width, height);
        DecodeInto(ref decoder, contexts, template, typicalPrediction, adaptive, bitmap, skip);
        return bitmap;
    }

    private static void DecodeInto(
        ref MqDecoder decoder,
        Span<byte> contexts,
        int template,
        bool typicalPrediction,
        ReadOnlySpan<sbyte> adaptive,
        Jbig2Bitmap bitmap,
        Jbig2Bitmap? skip)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int n2 = Row2Size[template];
        int n1 = Row1Size[template];
        int n0 = Row0Size[template];
        int right2 = Row2Right[template];
        int right1 = Row1Right[template];
        int mask2 = (1 << n2) - 1;
        int mask1 = (1 << n1) - 1;
        int mask0 = (1 << n0) - 1;
        int shift2 = n1 + n0;

        int adaptiveCount = AdaptiveBits[template].Length;
        if (adaptive.Length < adaptiveCount * 2)
        {
            throw new Jbig2DecodingException("JBIG2 generic region has too few adaptive template pixels.");
        }

        int customMask = 0;
        bool custom = false;
        for (int i = 0; i < adaptiveCount; i++)
        {
            if (adaptive[i * 2] != NominalX[template][i] || adaptive[(i * 2) + 1] != NominalY[template][i])
            {
                customMask |= 1 << AdaptiveBits[template][i];
                custom = true;
            }
        }

        int sltpContext = SltpContexts[template];
        bool lineIsTypical = false;
        var data = bitmap.Data;

        for (int y = 0; y < height; y++)
        {
            var row0 = bitmap.Row(y);
            if (typicalPrediction)
            {
                lineIsTypical ^= decoder.Decode(ref contexts[sltpContext]) != 0;
                if (lineIsTypical)
                {
                    if (y > 0)
                    {
                        bitmap.Row(y - 1).CopyTo(row0);
                    }

                    continue;
                }
            }

            ReadOnlySpan<byte> row1 = y >= 1 ? bitmap.Row(y - 1) : default;
            ReadOnlySpan<byte> row2 = y >= 2 ? bitmap.Row(y - 2) : default;

            int w2 = 0;
            for (int i = 0; i <= right2 && n2 > 0; i++)
            {
                w2 = (w2 << 1) | Pixel(row2, i);
            }

            int w1 = 0;
            for (int i = 0; i <= right1; i++)
            {
                w1 = (w1 << 1) | Pixel(row1, i);
            }

            int w0 = 0;
            for (int x = 0; x < width; x++)
            {
                int bit;
                if (skip is not null && skip.Data[(y * width) + x] != 0)
                {
                    bit = 0;
                }
                else
                {
                    int context = (w2 << shift2) | (w1 << n0) | w0;
                    if (custom)
                    {
                        context &= ~customMask;
                        for (int i = 0; i < adaptiveCount; i++)
                        {
                            if ((customMask & (1 << AdaptiveBits[template][i])) != 0)
                            {
                                int ax = x + adaptive[i * 2];
                                int ay = y + adaptive[(i * 2) + 1];
                                if (ay >= 0 && ay <= y && (uint)ax < (uint)width && (ay < y || ax < x))
                                {
                                    context |= data[(ay * width) + ax] << AdaptiveBits[template][i];
                                }
                            }
                        }
                    }

                    bit = decoder.Decode(ref contexts[context]);
                }

                row0[x] = (byte)bit;
                if (n2 > 0)
                {
                    w2 = ((w2 << 1) | Pixel(row2, x + right2 + 1)) & mask2;
                }

                w1 = ((w1 << 1) | Pixel(row1, x + right1 + 1)) & mask1;
                w0 = ((w0 << 1) | bit) & mask0;
            }
        }
    }

    private static int Pixel(ReadOnlySpan<byte> row, int x) => (uint)x < (uint)row.Length ? row[x] : 0;

    /// <summary>Decodes an MMR-coded (CCITT Group 4) bitmap, reporting how many bytes of <paramref name="data"/> it used.</summary>
    public static Jbig2Bitmap DecodeMmr(ReadOnlySpan<byte> data, int width, int height, out int consumed)
    {
        var bitmap = new Jbig2Bitmap(width, height);
        if (width == 0 || height == 0)
        {
            consumed = 0;
            return bitmap;
        }

        CcittResult result;
        try
        {
            result = CcittDecoder.Decode(data, new CcittParameters { K = -1, Columns = width, EndOfBlock = true }, height);
        }
        catch (CcittDataException ex)
        {
            throw new Jbig2DecodingException("JBIG2 MMR data is malformed: " + ex.Message, ex);
        }
        catch (CcittUnsupportedException ex)
        {
            throw new Jbig2UnsupportedFeatureException(ex.Message, ex);
        }

        for (int y = 0; y < height; y++)
        {
            BilevelExpander.ExpandRowToBits(result.Bits.AsSpan(y * result.Stride, result.Stride), bitmap.Row(y), width);
        }

        consumed = result.BytesConsumed;
        return bitmap;
    }
}
