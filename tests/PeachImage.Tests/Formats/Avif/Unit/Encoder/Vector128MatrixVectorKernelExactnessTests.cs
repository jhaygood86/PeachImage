using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PeachImage.Formats.Avif.Encoder.Av1.Transform;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// The four-rows-at-a-time <see cref="Vector128Av1MatrixVectorKernel"/> must produce the exact bits the one-row
/// form did: same products, same lane-wise accumulation order, same horizontal add. A floating-point test with a
/// tolerance would hide a reassociation, so this compares raw bit patterns against a reference copy of the
/// one-row loop.
/// </summary>
public class Vector128MatrixVectorKernelExactnessTests
{
    private static void OneRowReference(double[,] matrix, ReadOnlySpan<double> input, Span<double> output, int size)
    {
        for (int row = 0; row < size; row++)
        {
            ref double rowStart = ref matrix[row, 0];
            ReadOnlySpan<double> rowSpan = MemoryMarshal.CreateReadOnlySpan(ref rowStart, size);

            var acc = Vector128<double>.Zero;
            int col = 0;
            for (; col + 2 <= size; col += 2)
            {
                acc += Vector128.Create(rowSpan.Slice(col, 2)) * Vector128.Create(input.Slice(col, 2));
            }

            double sum = Vector128.Sum(acc);
            for (; col < size; col++)
            {
                sum += rowSpan[col] * input[col];
            }

            output[row] = sum;
        }
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(8, 2)]
    [InlineData(16, 3)]
    [InlineData(32, 4)]
    [InlineData(64, 5)]
    [InlineData(6, 6)] // Not a multiple of four rows: exercises the remainder-row loop.
    [InlineData(7, 7)] // Odd size: remainder rows and the scalar column tail.
    [InlineData(3, 8)]
    public void Apply_IsBitIdenticalToTheOneRowForm(int size, int seed)
    {
        Assert.SkipUnless(Vector128.IsHardwareAccelerated, "Vector128 is not hardware accelerated here.");

        var rng = new Random(seed);
        var kernel = new Vector128Av1MatrixVectorKernel();
        for (int iteration = 0; iteration < 300; iteration++)
        {
            var matrix = new double[size, size];
            var input = new double[size];
            for (int r = 0; r < size; r++)
            {
                input[r] = (rng.NextDouble() - 0.5) * (iteration % 3 == 0 ? 1e6 : 512);
                for (int c = 0; c < size; c++)
                {
                    matrix[r, c] = (rng.NextDouble() - 0.5) * (iteration % 5 == 0 ? 1e-3 : 2);
                }
            }

            var expected = new double[size];
            var actual = new double[size];
            OneRowReference(matrix, input, expected, size);
            kernel.Apply(matrix, input, actual, size);

            for (int r = 0; r < size; r++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[r]), BitConverter.DoubleToInt64Bits(actual[r]));
            }
        }
    }
}
