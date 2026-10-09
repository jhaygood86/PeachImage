using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlDequantKernelTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(67)]
    public void Dequantize_MatchesTheScalarReference(int size)
    {
        var random = new Random(7);
        int[][] q = [new int[size], new int[size], new int[size]];
        for (int c = 0; c < 3; c++)
        {
            for (int k = 0; k < size; k++)
            {
                q[c][k] = random.Next(5) == 0 ? random.Next(-3, 4) : random.Next(-200, 201);
            }
        }

        float[] matrices = new float[3 * size];
        for (int i = 0; i < matrices.Length; i++)
        {
            matrices[i] = 0.001f + (float)random.NextDouble();
        }

        float[] biases = [0.9f, 0.94f, 0.96f, 0.5f];
        float[] dx = new float[size], dy = new float[size], db = new float[size];
        DequantKernel.Dequantize(q[0], q[1], q[2], matrices, size, 0.7f, 0.9f, 1.3f, biases, 0.2f, -0.4f, dx, dy, db);

        for (int k = 0; k < size; k++)
        {
            float x = DequantKernel.AdjustQuantBias(0, q[0][k], biases) * (matrices[k] * 0.7f);
            float y = DequantKernel.AdjustQuantBias(1, q[1][k], biases) * (matrices[size + k] * 0.9f);
            float b = DequantKernel.AdjustQuantBias(2, q[2][k], biases) * (matrices[(2 * size) + k] * 1.3f);
            Assert.Equal((0.2f * y) + x, dx[k], 5);
            Assert.Equal(y, dy[k], 5);
            Assert.Equal((-0.4f * y) + b, db[k], 5);
        }
    }
}
