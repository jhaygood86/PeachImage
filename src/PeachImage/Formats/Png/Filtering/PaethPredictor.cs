namespace PeachImage.Formats.Png.Filtering;

/// <summary>The Paeth predictor function (spec §6.6), shared by the Paeth filter/unfilter and the encoder's candidate scoring.</summary>
internal static class PaethPredictor
{
    public static byte Predict(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }
}
