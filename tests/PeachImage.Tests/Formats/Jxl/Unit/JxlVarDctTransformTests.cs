using PeachImage.Formats.Jxl.Encoder.VarDct;
using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Tests.Formats.Jxl.Unit;

/// <summary>The encoder's forward block transforms must invert the decoder's, including the DC image standing in for the lowest frequencies.</summary>
public class JxlVarDctTransformTests
{
    public static TheoryData<int> Strategies()
    {
        var data = new TheoryData<int>();
        foreach (int strategy in JxlVarDctTransform.Supported)
        {
            data.Add(strategy);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public void Forward_IsInvertedByTheDecodersIdct(int strategy)
    {
        (int rows, int cols, float[] pixels, float[] coefficients) = Transform(strategy);

        var restored = new float[rows * cols];
        var scratch = new float[5 * rows * cols];
        DctTransforms.TransformToPixels(strategy, coefficients.AsSpan(), restored, cols, scratch);

        for (int i = 0; i < pixels.Length; i++)
        {
            Assert.True(Math.Abs(pixels[i] - restored[i]) < 1e-3f, $"Pixel {i}: {pixels[i]} vs {restored[i]}");
        }
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public void LowestFrequenciesToDc_IsInvertedByTheDecoder(int strategy)
    {
        (int rows, int cols, _, float[] coefficients) = Transform(strategy);
        int cx = AcStrategy.CoveredBlocksX(strategy);
        int cy = AcStrategy.CoveredBlocksY(strategy);

        var dc = new float[cx * cy];
        JxlVarDctTransform.LowestFrequenciesToDc(strategy, coefficients, dc);

        var llf = new float[rows * cols];
        DctTransforms.LowestFrequenciesFromDc(strategy, dc, cx, llf);

        bool wide = rows < cols;
        int stride = Math.Max(rows, cols);
        for (int ky = 0; ky < cy; ky++)
        {
            for (int kx = 0; kx < cx; kx++)
            {
                int index = wide ? (ky * stride) + kx : (kx * stride) + ky;
                Assert.True(Math.Abs(coefficients[index] - llf[index]) < 1e-3f, $"LLF ({ky},{kx}): {coefficients[index]} vs {llf[index]}");
            }
        }
    }

    private static (int Rows, int Cols, float[] Pixels, float[] Coefficients) Transform(int strategy)
    {
        int rows = JxlVarDctTransform.Rows(strategy);
        int cols = JxlVarDctTransform.Cols(strategy);
        var random = new Random(strategy + 1);
        var pixels = new float[rows * cols];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                pixels[(y * cols) + x] = (float)((Math.Sin(x * 0.3) * Math.Cos(y * 0.2)) + (random.NextDouble() * 0.3));
            }
        }

        var coefficients = new float[rows * cols];
        JxlVarDctTransform.Forward(strategy, pixels, 0, cols, coefficients, new float[rows * cols]);
        return (rows, cols, pixels, coefficients);
    }
}
