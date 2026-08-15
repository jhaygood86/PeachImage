using PeachImage.Formats.Webp.Decoding.Vp8.Dct;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Independently verifies <see cref="Vp8ScalarInverseWht"/> before it is wired into the full decode pipeline.
/// Working through the butterfly's index arithmetic by hand (see the class remarks below) shows both of its
/// passes apply the exact same natural-order 4x4 Hadamard matrix H = [[1,1,1,1],[1,1,-1,-1],[1,-1,-1,1],
/// [1,-1,1,-1]] (first pass over columns, second pass over rows, i.e. a 2D transform Y = H.X.H), with only the
/// second pass adding the +3/&gt;&gt;3 rounding. H is symmetric and self-inverse up to scale (H*H = 4*Identity) -
/// the classic "self-inverse" Hadamard property - which this file checks two ways: first as a pure fact about
/// the matrix itself (independent of any of this decoder's code), then by checking that
/// <see cref="Vp8ScalarInverseWht.Transform"/>'s actual output matches a brute-force <c>round((H.C.H)/8)</c>
/// matrix computation element-for-element - which would catch a transposition or index-arithmetic bug that a
/// looser numerical test could miss.
/// </summary>
public class Vp8ScalarInverseWhtTests
{
    private static readonly int[,] H =
    {
        { 1, 1, 1, 1 },
        { 1, 1, -1, -1 },
        { 1, -1, -1, 1 },
        { 1, -1, 1, -1 },
    };

    [Fact]
    public void HadamardMatrix_IsSelfInverseUpToScale_HTimesHEqualsFourIdentity()
    {
        int[,] product = MatrixMultiply(H, H);

        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                int expected = r == c ? 4 : 0;
                Assert.Equal(expected, product[r, c]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(InputCases))]
    public void Transform_MatchesBruteForceHadamardMatrixMultiplication(short[] input)
    {
        var blockDc = new short[16];
        Vp8ScalarInverseWht.Transform(input, blockDc);

        // Reshape the flat 16-coefficient input into a 4x4 matrix (row-major, matching how the decoder lays
        // out dequantized coefficients), apply Y = H.X.H by brute force, then round exactly as the transform's
        // second pass does: (sum + 3) >> 3.
        int[,] x = new int[4, 4];
        for (int i = 0; i < 16; i++)
        {
            x[i / 4, i % 4] = input[i];
        }

        int[,] hx = MatrixMultiply(H, x);
        int[,] hxh = MatrixMultiply(hx, H);

        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                short expected = (short)((hxh[row, col] + 3) >> 3);
                short actual = blockDc[(4 * row) + col];
                Assert.Equal(expected, actual);
            }
        }
    }

    public static TheoryData<short[]> InputCases()
    {
        var data = new TheoryData<short[]>();
        data.Add(new short[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        data.Add(new short[] { 40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        data.Add(new short[] { 16, -8, 4, -2, 1, -1, 3, 5, -6, 7, -3, 2, 0, -4, 9, -5 });
        data.Add(new short[] { -200, 150, -100, 75, -50, 25, -12, 6, 3, -1, 8, -9, 11, -13, 17, -19 });
        return data;
    }

    private static int[,] MatrixMultiply(int[,] a, int[,] b)
    {
        var result = new int[4, 4];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += a[r, k] * b[k, c];
                }

                result[r, c] = sum;
            }
        }

        return result;
    }
}