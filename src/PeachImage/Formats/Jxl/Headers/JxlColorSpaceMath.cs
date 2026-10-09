namespace PeachImage.Formats.Jxl.Headers;

/// <summary>
/// Chromaticity arithmetic for XYB output in colour spaces other than sRGB: the linear-light matrix from sRGB primaries to the
/// image's primaries (through XYZ with Bradford adaptation to D50, as the reference decoder does), and the luminance weights
/// of the image's primaries.
/// </summary>
internal static class JxlColorSpaceMath
{
    private static readonly double[] D50 = [0.96422, 1.0, 0.82521];

    private static readonly double[][] Bradford =
    [
        [0.8951, 0.2664, -0.1614],
        [-0.7502, 1.7135, 0.0367],
        [0.0389, -0.0685, 1.0296],
    ];

    /// <summary>Whether the encoding's primaries and white point are exactly those of sRGB (so no matrix is needed).</summary>
    public static bool IsSrgbGamut(JxlColorEncoding encoding) =>
        encoding.ColorSpace != JxlColorSpace.Rgb || (encoding.Primaries == JxlPrimaries.Srgb && encoding.WhitePoint == JxlWhitePoint.D65);

    /// <summary>Row-major 3x3 matrix converting linear sRGB to linear RGB in the encoding's primaries and white point.</summary>
    public static float[] SrgbToEncoding(JxlColorEncoding encoding)
    {
        var (srgbPrimaries, srgbWhite) = Chromaticities(JxlPrimaries.Srgb, default, default, default, JxlWhitePoint.D65, default);
        double[][] srgbToD50 = Multiply(AdaptToD50(srgbWhite), PrimariesToXyz(srgbPrimaries, srgbWhite));

        var (primaries, white) = Chromaticities(encoding.Primaries, encoding.CustomRed, encoding.CustomGreen, encoding.CustomBlue, encoding.WhitePoint, encoding.CustomWhitePoint);
        double[][] encodingToD50 = Multiply(AdaptToD50(white), PrimariesToXyz(primaries, white));
        double[][] result = Multiply(Invert(encodingToD50), srgbToD50);
        return Flatten(result);
    }

    /// <summary>The luminance (Y) weights of the encoding's red, green and blue primaries relative to its own white point.</summary>
    public static float[] Luminances(JxlColorEncoding encoding)
    {
        if (IsSrgbGamut(encoding))
        {
            return [0.2126f, 0.7152f, 0.0722f];
        }

        var (primaries, white) = Chromaticities(encoding.Primaries, encoding.CustomRed, encoding.CustomGreen, encoding.CustomBlue, encoding.WhitePoint, encoding.CustomWhitePoint);
        double[][] xyz = PrimariesToXyz(primaries, white);
        return [(float)xyz[1][0], (float)xyz[1][1], (float)xyz[1][2]];
    }

    private static (double[][] Primaries, double[] White) Chromaticities(
        JxlPrimaries primaries,
        JxlCustomXy red,
        JxlCustomXy green,
        JxlCustomXy blue,
        JxlWhitePoint whitePoint,
        JxlCustomXy customWhite)
    {
        double[][] p = primaries switch
        {
            JxlPrimaries.Srgb => [[0.639998686, 0.330010138], [0.300003784, 0.600003357], [0.150002046, 0.059997204]],
            JxlPrimaries.Bt2100 => [[0.708, 0.292], [0.170, 0.797], [0.131, 0.046]],
            JxlPrimaries.P3 => [[0.680, 0.320], [0.265, 0.690], [0.150, 0.060]],
            _ => [[red.XValue, red.YValue], [green.XValue, green.YValue], [blue.XValue, blue.YValue]],
        };
        double[] w = whitePoint switch
        {
            JxlWhitePoint.D65 => [0.3127, 0.3290],
            JxlWhitePoint.E => [1.0 / 3.0, 1.0 / 3.0],
            JxlWhitePoint.Dci => [0.314, 0.351],
            _ => [customWhite.XValue, customWhite.YValue],
        };
        return (p, w);
    }

    // Columns are the primaries' XYZ scaled so that RGB (1, 1, 1) is the white point (Y = 1).
    private static double[][] PrimariesToXyz(double[][] primaries, double[] white)
    {
        double[][] columns = new double[3][];
        for (int c = 0; c < 3; c++)
        {
            double x = primaries[c][0];
            double y = primaries[c][1];
            columns[c] = [x / y, 1.0, (1.0 - x - y) / y];
        }

        double[][] matrix = [[columns[0][0], columns[1][0], columns[2][0]], [columns[0][1], columns[1][1], columns[2][1]], [columns[0][2], columns[1][2], columns[2][2]]];
        double[] whiteXyz = [white[0] / white[1], 1.0, (1.0 - white[0] - white[1]) / white[1]];
        double[][] inverse = Invert(matrix);
        double[] scale = new double[3];
        for (int r = 0; r < 3; r++)
        {
            scale[r] = (inverse[r][0] * whiteXyz[0]) + (inverse[r][1] * whiteXyz[1]) + (inverse[r][2] * whiteXyz[2]);
        }

        return
        [
            [matrix[0][0] * scale[0], matrix[0][1] * scale[1], matrix[0][2] * scale[2]],
            [matrix[1][0] * scale[0], matrix[1][1] * scale[1], matrix[1][2] * scale[2]],
            [matrix[2][0] * scale[0], matrix[2][1] * scale[1], matrix[2][2] * scale[2]],
        ];
    }

    // Bradford chromatic adaptation from the given white point to D50.
    private static double[][] AdaptToD50(double[] white)
    {
        double[] source = [white[0] / white[1], 1.0, (1.0 - white[0] - white[1]) / white[1]];
        double[] sourceCone = Apply(Bradford, source);
        double[] targetCone = Apply(Bradford, D50);
        double[][] scale =
        [
            [targetCone[0] / sourceCone[0], 0, 0],
            [0, targetCone[1] / sourceCone[1], 0],
            [0, 0, targetCone[2] / sourceCone[2]],
        ];
        return Multiply(Invert(Bradford), Multiply(scale, Bradford));
    }

    private static double[] Apply(double[][] m, double[] v) =>
        [(m[0][0] * v[0]) + (m[0][1] * v[1]) + (m[0][2] * v[2]), (m[1][0] * v[0]) + (m[1][1] * v[1]) + (m[1][2] * v[2]), (m[2][0] * v[0]) + (m[2][1] * v[1]) + (m[2][2] * v[2])];

    private static double[][] Multiply(double[][] a, double[][] b)
    {
        var result = new double[3][];
        for (int r = 0; r < 3; r++)
        {
            result[r] = new double[3];
            for (int c = 0; c < 3; c++)
            {
                result[r][c] = (a[r][0] * b[0][c]) + (a[r][1] * b[1][c]) + (a[r][2] * b[2][c]);
            }
        }

        return result;
    }

    private static double[][] Invert(double[][] m)
    {
        double det = (m[0][0] * ((m[1][1] * m[2][2]) - (m[1][2] * m[2][1])))
            - (m[0][1] * ((m[1][0] * m[2][2]) - (m[1][2] * m[2][0])))
            + (m[0][2] * ((m[1][0] * m[2][1]) - (m[1][1] * m[2][0])));
        if (Math.Abs(det) < 1e-12)
        {
            throw new JxlUnsupportedFeatureException("The colour primaries of the image are degenerate.");
        }

        double inv = 1.0 / det;
        return
        [
            [((m[1][1] * m[2][2]) - (m[1][2] * m[2][1])) * inv, ((m[0][2] * m[2][1]) - (m[0][1] * m[2][2])) * inv, ((m[0][1] * m[1][2]) - (m[0][2] * m[1][1])) * inv],
            [((m[1][2] * m[2][0]) - (m[1][0] * m[2][2])) * inv, ((m[0][0] * m[2][2]) - (m[0][2] * m[2][0])) * inv, ((m[0][2] * m[1][0]) - (m[0][0] * m[1][2])) * inv],
            [((m[1][0] * m[2][1]) - (m[1][1] * m[2][0])) * inv, ((m[0][1] * m[2][0]) - (m[0][0] * m[2][1])) * inv, ((m[0][0] * m[1][1]) - (m[0][1] * m[1][0])) * inv],
        ];
    }

    private static float[] Flatten(double[][] m) =>
        [(float)m[0][0], (float)m[0][1], (float)m[0][2], (float)m[1][0], (float)m[1][1], (float)m[1][2], (float)m[2][0], (float)m[2][1], (float)m[2][2]];
}
