using PeachImage.Formats.Jxl.Bitstream;

namespace PeachImage.Formats.Jxl.Headers;

/// <summary>
/// The codestream's <c>CustomTransformData</c> bundle (read after <see cref="JxlImageMetadata"/>): the inverse
/// opsin matrix used for XYB decoding and optional custom 2x/4x/8x upsampling kernels. Null members mean "use the default".
/// </summary>
internal sealed class JxlCustomTransformData
{
    /// <summary>The 3x3 inverse opsin absorbance matrix, row-major; defaults when all_default.</summary>
    public float[] InverseMatrix { get; private init; } = DefaultInverseMatrix.ToArray();

    /// <summary>The three opsin biases (negated absorbance bias).</summary>
    public float[] OpsinBiases { get; private init; } = DefaultOpsinBiases.ToArray();

    /// <summary>The four quantization biases.</summary>
    public float[] QuantBiases { get; private init; } = DefaultQuantBiases.ToArray();

    /// <summary>The 15 unique weights of the 2x upsampling kernel, or null for the default.</summary>
    public float[]? Upsampling2Weights { get; private init; }

    /// <summary>The 55 unique weights of the 4x upsampling kernel, or null for the default.</summary>
    public float[]? Upsampling4Weights { get; private init; }

    /// <summary>The 210 unique weights of the 8x upsampling kernel, or null for the default.</summary>
    public float[]? Upsampling8Weights { get; private init; }

    public static ReadOnlySpan<float> DefaultInverseMatrix =>
    [
        11.031566901960783f, -9.866943921568629f, -0.16462299647058826f,
        -3.254147380392157f, 4.418770392156863f, -0.16462299647058826f,
        -3.6588512862745097f, 2.7129230470588235f, 1.9459282392156863f,
    ];

    public static ReadOnlySpan<float> DefaultOpsinBiases => [-0.0037930732552754493f, -0.0037930732552754493f, -0.0037930732552754493f];

    public static ReadOnlySpan<float> DefaultQuantBiases =>
    [
        1.0f - 0.05465007330715401f,
        1.0f - 0.07005449891748593f,
        1.0f - 0.049935103337343655f,
        0.145f,
    ];

    public static JxlCustomTransformData Read(ref JxlBitReader reader, bool xybEncoded)
    {
        if (reader.ReadBool())
        {
            return new JxlCustomTransformData();
        }

        float[] matrix = DefaultInverseMatrix.ToArray();
        float[] biases = DefaultOpsinBiases.ToArray();
        float[] quant = DefaultQuantBiases.ToArray();
        if (xybEncoded)
        {
            // OpsinInverseMatrix has its own all_default flag.
            if (!reader.ReadBool())
            {
                for (int i = 0; i < matrix.Length; i++)
                {
                    matrix[i] = JxlFieldReader.ReadF16(ref reader);
                }

                for (int i = 0; i < biases.Length; i++)
                {
                    biases[i] = JxlFieldReader.ReadF16(ref reader);
                }

                for (int i = 0; i < quant.Length; i++)
                {
                    quant[i] = JxlFieldReader.ReadF16(ref reader);
                }
            }
        }

        uint mask = reader.ReadBits(3);
        float[]? up2 = (mask & 1) != 0 ? ReadWeights(ref reader, 15) : null;
        float[]? up4 = (mask & 2) != 0 ? ReadWeights(ref reader, 55) : null;
        float[]? up8 = (mask & 4) != 0 ? ReadWeights(ref reader, 210) : null;
        reader.ThrowIfOverrun();

        return new JxlCustomTransformData
        {
            InverseMatrix = matrix,
            OpsinBiases = biases,
            QuantBiases = quant,
            Upsampling2Weights = up2,
            Upsampling4Weights = up4,
            Upsampling8Weights = up8,
        };
    }

    private static float[] ReadWeights(ref JxlBitReader reader, int count)
    {
        var weights = new float[count];
        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] = JxlFieldReader.ReadF16(ref reader);
        }

        return weights;
    }
}
