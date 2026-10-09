using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlFastIdctTests
{
    [Theory]
    [InlineData(2, 3)]
    [InlineData(4, 8)]
    [InlineData(8, 8)]
    [InlineData(8, 13)]
    [InlineData(16, 16)]
    [InlineData(32, 32)]
    [InlineData(64, 5)]
    [InlineData(128, 8)]
    [InlineData(256, 16)]
    public void MatchesTheDefinitionOfTheInverseDct(int size, int width)
    {
        var random = new Random(size * 1000 + width);
        var input = new float[size * width];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)((random.NextDouble() * 2) - 1);
        }

        const int outputStride = 300;
        var output = new float[size * outputStride];
        var work = new float[FastIdct.WorkSize(size, width)];

        FastIdct.Transform(size, input, output, outputStride, width, work);

        for (int n = 0; n < size; n++)
        {
            for (int j = 0; j < width; j++)
            {
                double expected = input[j];
                for (int k = 1; k < size; k++)
                {
                    expected += Math.Sqrt(2.0) * input[(k * width) + j] * Math.Cos((n + 0.5) * k * Math.PI / size);
                }

                Assert.InRange(output[(n * outputStride) + j], expected - 1e-3, expected + 1e-3);
            }
        }
    }
}
