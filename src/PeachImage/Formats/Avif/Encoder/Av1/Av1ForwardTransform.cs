using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1.Transform;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Forward 2D DCT for AV1 encoding, restricted to DCT_DCT on square transform sizes 4x4/8x8/16x16/32x32 --
/// this encoder's v1 tx-size/tx-type scope (<c>tx_mode = TX_MODE_LARGEST</c>, <c>reduced_tx_set = true</c>,
/// see <see cref="Av1FrameHeaderWriter"/>). AV1's spec only normatively defines the <em>inverse</em>
/// transform (as with every video codec) -- an encoder's forward transform is free-form as long as it
/// round-trips acceptably through the normative inverse. Rather than hand-deriving the transpose of
/// <see cref="Av1InverseTransform"/>'s spec-mandated fixed-point butterfly network (a well-known but
/// error-prone derivation), this numerically constructs the forward transform as the matrix inverse of the
/// exact row/column operators <see cref="Av1InverseTransform.Inverse2D"/> applies -- built once, at
/// class-init time, by probing <see cref="Av1InverseTransform.InverseDct"/> with impulse vectors. This
/// guarantees <c>Inverse2D(Forward2D(x)) &#8776; x</c> by construction (up to the small integer-rounding
/// noise inherent to probing a rounding fixed-point operator, negligible next to real quantization noise),
/// verified directly by this class's own round-trip tests against the existing decoder.
/// </summary>
internal static class Av1ForwardTransform
{
    /// <summary><c>Transform_Row_Shift</c> (spec §7.13.3) values for the four square sizes this encoder uses -- copied from <see cref="Av1InverseTransform"/>'s own table at indices <see cref="Av1TxSize.Tx4x4"/>-<see cref="Av1TxSize.Tx32x32"/>.</summary>
    private static readonly int[] RowShiftBySize = [0, 1, 2, 2];

    private const int ColShift = 4;
    private const int ClampRange = 16; // bitDepth(8) + 8 == max(bitDepth(8) + 6, 16) == 16 for both row and column passes at 8-bit.
    private const double ImpulseAmplitude = 4096.0;

    private static readonly double[][,] RowInverseMatrices = BuildOperators(useRowShift: true);
    private static readonly double[][,] ColInverseMatrices = BuildOperators(useRowShift: false);

    /// <summary>
    /// Forward-transforms <paramref name="residual"/> (a flat <paramref name="size"/> x <paramref name="size"/>
    /// row-major buffer, e.g. <c>source - prediction</c>) into <paramref name="coeffOut"/> (same shape and
    /// layout), for one of the four supported square DCT_DCT sizes (4, 8, 16, or 32). Output is in the same
    /// domain <see cref="Av1Dequantizer.Dequantize"/> produces (i.e. what <see cref="Av1InverseTransform.Inverse2D"/>
    /// expects as its <c>dequant</c> input) -- <c>Av1ForwardQuantizer</c> is responsible for the forward
    /// quantization step down to entropy-codable levels.
    /// </summary>
    public static void Forward2D(ReadOnlySpan<int> residual, Span<int> coeffOut, int size)
    {
        int txSz = SizeToTxSz(size);
        double[,] rowInverse = RowInverseMatrices[txSz];
        double[,] colInverse = ColInverseMatrices[txSz];

        // Fixed-max-literal stackalloc (32 is the largest supported square size, so size*size <= 1024)
        // sliced down to what this call actually needs -- avoids a heap allocation on every single block.
        Span<double> intermediateBuffer = stackalloc double[1024];
        Span<double> intermediate = intermediateBuffer[..(size * size)];

        Span<double> rowBuf = stackalloc double[size];
        Span<double> rowOut = stackalloc double[size];
        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                rowBuf[j] = residual[(i * size) + j];
            }

            ApplyMatrix(rowInverse, rowBuf, rowOut, size);

            for (int j = 0; j < size; j++)
            {
                intermediate[(i * size) + j] = rowOut[j];
            }
        }

        Span<double> colBuf = stackalloc double[size];
        Span<double> colOut = stackalloc double[size];
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                colBuf[i] = intermediate[(i * size) + j];
            }

            ApplyMatrix(colInverse, colBuf, colOut, size);

            for (int i = 0; i < size; i++)
            {
                coeffOut[(i * size) + j] = (int)Math.Round(colOut[i]);
            }
        }
    }

    /// <summary>Delegates to <see cref="Av1MatrixVectorKernelSelector.Instance"/> -- see <see cref="Transform.IAv1MatrixVectorKernel"/>'s remarks for why this row/column dot product gets its own narrow SIMD-tiered kernel rather than staying inline.</summary>
    private static void ApplyMatrix(double[,] matrix, ReadOnlySpan<double> input, Span<double> output, int size)
        => Av1MatrixVectorKernelSelector.Instance.Apply(matrix, input, output, size);

    /// <summary>Maps a square block size (4/8/16/32) to its <see cref="Av1TxSize"/> constant. Shared with <c>Av1ForwardQuantizer</c> and <c>Av1LocalReconstructor</c>, which need the same mapping to drive <see cref="Av1Dequantizer"/>/<see cref="Av1InverseTransform"/>.</summary>
    internal static int SizeToTxSz(int size) => size switch
    {
        4 => Av1TxSize.Tx4x4,
        8 => Av1TxSize.Tx8x8,
        16 => Av1TxSize.Tx16x16,
        32 => Av1TxSize.Tx32x32,
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Av1ForwardTransform only supports square sizes 4, 8, 16, or 32."),
    };

    /// <summary>Builds, for each of the four square tx sizes, the numeric inverse of the row (or column) operator <see cref="Av1InverseTransform.Inverse2D"/> applies -- indexed by <see cref="Av1TxSize"/> constant.</summary>
    private static double[][,] BuildOperators(bool useRowShift)
    {
        var operators = new double[4][,];
        ReadOnlySpan<int> txSizes = [Av1TxSize.Tx4x4, Av1TxSize.Tx8x8, Av1TxSize.Tx16x16, Av1TxSize.Tx32x32];
        foreach (int txSz in txSizes)
        {
            int log2Size = Av1TxDimensions.WidthLog2[txSz]; // Width == Height for all four sizes (square only).
            int size = 1 << log2Size;
            int shift = useRowShift ? RowShiftBySize[txSz] : ColShift;

            double[,] forwardOperatorMatrix = BuildImpulseResponseMatrix(log2Size, size, shift);
            operators[txSz] = Invert(forwardOperatorMatrix, size);
        }

        return operators;
    }

    /// <summary>Probes <see cref="Av1InverseTransform.InverseDct"/> with unit impulses to build the matrix of the operator <c>v =&gt; Round2(InverseDct(v), shift)</c> as a linear approximation (impulse superposition ignores the small rounding nonlinearity within <c>InverseDct</c> itself, which is negligible at <see cref="ImpulseAmplitude"/>'s scale).</summary>
    private static double[,] BuildImpulseResponseMatrix(int log2Size, int size, int shift)
    {
        var matrix = new double[size, size];
        var probe = new int[size];

        for (int k = 0; k < size; k++)
        {
            Array.Clear(probe);
            probe[k] = (int)ImpulseAmplitude;
            Av1InverseTransform.InverseDct(probe, log2Size, ClampRange);

            for (int outIdx = 0; outIdx < size; outIdx++)
            {
                matrix[outIdx, k] = Round2(probe[outIdx], shift) / ImpulseAmplitude;
            }
        }

        return matrix;
    }

    /// <summary><c>Round2(x, n)</c> (spec §4.7), matching <see cref="Av1InverseTransform"/>'s own private helper.</summary>
    private static int Round2(int x, int n) => n == 0 ? x : (int)(((long)x + (1L << (n - 1))) >> n);

    /// <summary>Gauss-Jordan matrix inversion with partial pivoting. Sizes here never exceed 32x32, so plain O(size^3) elimination is more than fast enough.</summary>
    private static double[,] Invert(double[,] matrix, int size)
    {
        var augmented = new double[size, size * 2];
        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                augmented[i, j] = matrix[i, j];
            }

            augmented[i, size + i] = 1.0;
        }

        for (int col = 0; col < size; col++)
        {
            int pivotRow = col;
            double maxAbs = Math.Abs(augmented[col, col]);
            for (int row = col + 1; row < size; row++)
            {
                double abs = Math.Abs(augmented[row, col]);
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                    pivotRow = row;
                }
            }

            if (pivotRow != col)
            {
                for (int j = 0; j < size * 2; j++)
                {
                    (augmented[col, j], augmented[pivotRow, j]) = (augmented[pivotRow, j], augmented[col, j]);
                }
            }

            double pivot = augmented[col, col];
            if (Math.Abs(pivot) < 1e-9)
            {
                throw new InvalidOperationException($"Av1ForwardTransform: the impulse-response probe of Av1InverseTransform produced a singular {size}x{size} operator matrix (near-zero pivot at column {col}) -- this should never happen for a real DCT operator.");
            }

            for (int j = 0; j < size * 2; j++)
            {
                augmented[col, j] /= pivot;
            }

            for (int row = 0; row < size; row++)
            {
                if (row == col)
                {
                    continue;
                }

                double factor = augmented[row, col];
                if (factor == 0)
                {
                    continue;
                }

                for (int j = 0; j < size * 2; j++)
                {
                    augmented[row, j] -= factor * augmented[col, j];
                }
            }
        }

        var inverse = new double[size, size];
        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                inverse[i, j] = augmented[i, size + j];
            }
        }

        return inverse;
    }
}
