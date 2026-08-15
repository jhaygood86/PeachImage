using PeachImage.Formats.Png.Filtering;

namespace PeachImage.Formats.Webp.Decoding.Alpha;

/// <summary>
/// Reverses the row-prediction filter (None/Horizontal/Vertical/Gradient) applied to a decompressed
/// <c>ALPH</c> alpha plane. Matches libwebp's <c>dsp/filters.c</c> reference behavior, including its least
/// obvious detail: for <see cref="WebpAlphaFilterMethod.Vertical"/> and <see cref="WebpAlphaFilterMethod.Gradient"/>,
/// row 0 (which has no row above) falls back to the <em>horizontal</em>, chained-predictor-starting-at-zero
/// reconstruction rather than being left untouched or using an all-zero predictor.
/// </summary>
internal static class WebpAlphaFilter
{
    public static void Reverse(WebpAlphaFilterMethod method, Span<byte> plane, int width, int height)
    {
        if (width <= 0 || height <= 0 || method == WebpAlphaFilterMethod.None)
        {
            return;
        }

        switch (method)
        {
            case WebpAlphaFilterMethod.Horizontal:
                for (int y = 0; y < height; y++)
                {
                    var row = plane.Slice(y * width, width);
                    byte initialPredictor = y == 0 ? (byte)0 : plane[(y - 1) * width];
                    ReverseHorizontalRow(row, initialPredictor);
                }

                return;

            case WebpAlphaFilterMethod.Vertical:
                ReverseHorizontalRow(plane[..width], 0);
                for (int y = 1; y < height; y++)
                {
                    var row = plane.Slice(y * width, width);
                    var previousRow = plane.Slice((y - 1) * width, width);
                    VectorizedRowFilter.UnfilterUp(row, previousRow);
                }

                return;

            case WebpAlphaFilterMethod.Gradient:
                ReverseHorizontalRow(plane[..width], 0);
                for (int y = 1; y < height; y++)
                {
                    ReverseGradientRow(plane, width, y);
                }

                return;
        }
    }

    /// <summary>Left-to-right chained reconstruction: each pixel's predictor is the just-reconstructed pixel to its left (or <paramref name="initialPredictor"/> for column 0).</summary>
    private static void ReverseHorizontalRow(Span<byte> row, byte initialPredictor)
    {
        byte predictor = initialPredictor;
        for (int x = 0; x < row.Length; x++)
        {
            row[x] = (byte)(row[x] + predictor);
            predictor = row[x];
        }
    }

    /// <summary>Row &gt;= 1 only: predictor is <c>Clip255(left + top - topLeft)</c>, no Paeth arg-min selection (unlike PNG's superficially similar Paeth filter).</summary>
    private static void ReverseGradientRow(Span<byte> plane, int width, int y)
    {
        var row = plane.Slice(y * width, width);
        var previousRow = plane.Slice((y - 1) * width, width);

        byte left = previousRow[0];
        byte topLeft = previousRow[0];

        for (int x = 0; x < width; x++)
        {
            byte top = previousRow[x];
            int predicted = Clip255(left + top - topLeft);
            row[x] = (byte)(row[x] + predicted);
            left = row[x];
            topLeft = top;
        }
    }

    private static byte Clip255(int value) => (byte)Math.Clamp(value, 0, 255);
}
