using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1.Transform;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Forward 2D DCT/ADST/IDTX for AV1 encoding, covering every <c>txType</c> this encoder's
/// reduced transform set (<c>reduced_tx_set = true</c>, always signalled -- see <see cref="Av1FrameHeaderWriter"/>)
/// can ever select for an intra block: <see cref="Av1TxType.DctDct"/>/<see cref="Av1TxType.AdstDct"/>/
/// <see cref="Av1TxType.DctAdst"/>/<see cref="Av1TxType.AdstAdst"/>/<see cref="Av1TxType.Idtx"/> -- see
/// <c>Av1TileDecoder.GetTxSet</c>'s remarks for why <c>reduced_tx_set</c> collapses this encoder's real search
/// space down to exactly TX_SET_INTRA_2 (those five, all <see cref="Av1TxClass.Class2D"/>) at every size that
/// reads a <c>tx_type</c> symbol at all (TX_4X4/8x8/16x16 -- TX_32X32 is forced <c>DCT_DCT</c> with no symbol
/// read, spec's <c>TX_SET_DCTONLY</c> short-circuit). DCT_DCT is supported at all four square sizes
/// (4/8/16/32, this encoder's v1 tx-size scope, <c>tx_mode = TX_MODE_LARGEST</c>); the four non-DCT_DCT types
/// only ever need sizes 4/8/16 -- AV1 has no ADST32, and IDTX never reaches TX_32X32 either given the
/// DCTONLY short-circuit above, so this class doesn't build (or need) ADST/IDTX operators at size 32.
///
/// <para>AV1's spec only normatively defines the <em>inverse</em> transform (as with every video codec) -- an
/// encoder's forward transform is free-form as long as it round-trips acceptably through the normative
/// inverse. Rather than hand-deriving the transpose of <see cref="Av1InverseTransform"/>'s spec-mandated
/// fixed-point butterfly network (a well-known but error-prone derivation) for each of DCT/ADST/IDTX
/// separately, this numerically constructs every forward operator the same way: the matrix inverse of the
/// exact row/column operator <see cref="Av1InverseTransform.Inverse2D"/> applies for that transform kind --
/// built once, at class-init time, by probing <see cref="Av1InverseTransform.InverseDct"/>,
/// <see cref="Av1InverseTransform.InverseAdst"/>, or <see cref="Av1InverseTransform.InverseIdentity"/> (one
/// shared <see cref="BuildOperators"/> helper, parameterized by which of the three to probe) with impulse
/// vectors. This guarantees <c>Inverse2D(Forward2D(x)) &#8776; x</c> by construction (up to the small
/// integer-rounding noise inherent to probing a rounding fixed-point operator, negligible next to real
/// quantization noise), verified directly by this class's own round-trip tests against the existing decoder.</para>
/// </summary>
internal static class Av1ForwardTransform
{
    /// <summary><c>Transform_Row_Shift</c> (spec §7.13.3) values for the four square sizes this encoder uses -- copied from <see cref="Av1InverseTransform"/>'s own table at indices <see cref="Av1TxSize.Tx4x4"/>-<see cref="Av1TxSize.Tx32x32"/>.</summary>
    private static readonly int[] RowShiftBySize = [0, 1, 2, 2];

    private const int ColShift = 4;
    private const int ClampRange = 16; // bitDepth(8) + 8 == max(bitDepth(8) + 6, 16) == 16 for both row and column passes at 8-bit.
    private const double ImpulseAmplitude = 4096.0;

    // Every square size (4/8/16/32) DCT_DCT ever needs.
    private static readonly int[] DctSizes = [4, 8, 16, 32];

    // AV1 has no ADST32, and TX_32X32 never reads a tx_type symbol at all (always DCT_DCT, see the class
    // remarks) -- so ADST and IDTX only ever need building at 4/8/16, not the full DctSizes set.
    private static readonly int[] Adst4To16Sizes = [4, 8, 16];

    private static readonly double[][,] RowInverseMatrices = BuildOperators(useRowShift: true, DctSizes, (t, n) => Av1InverseTransform.InverseDct(t, n, ClampRange));
    private static readonly double[][,] ColInverseMatrices = BuildOperators(useRowShift: false, DctSizes, (t, n) => Av1InverseTransform.InverseDct(t, n, ClampRange));

    private static readonly double[][,] RowAdstMatrices = BuildOperators(useRowShift: true, Adst4To16Sizes, (t, n) => Av1InverseTransform.InverseAdst(t, n, ClampRange));
    private static readonly double[][,] ColAdstMatrices = BuildOperators(useRowShift: false, Adst4To16Sizes, (t, n) => Av1InverseTransform.InverseAdst(t, n, ClampRange));

    private static readonly double[][,] RowIdentityMatrices = BuildOperators(useRowShift: true, Adst4To16Sizes, Av1InverseTransform.InverseIdentity);
    private static readonly double[][,] ColIdentityMatrices = BuildOperators(useRowShift: false, Adst4To16Sizes, Av1InverseTransform.InverseIdentity);

    /// <summary>
    /// Forward-transforms <paramref name="residual"/> (a flat <paramref name="size"/> x <paramref name="size"/>
    /// row-major buffer, e.g. <c>source - prediction</c>) into <paramref name="coeffOut"/> (same shape and
    /// layout), for one of the four supported square sizes (4, 8, 16, or 32) and any of the five
    /// <paramref name="txType"/> values this encoder's reduced transform set can select (see the class
    /// remarks) -- <see cref="Av1TxType.AdstDct"/>/<see cref="Av1TxType.DctAdst"/>/<see cref="Av1TxType.AdstAdst"/>/
    /// <see cref="Av1TxType.Idtx"/> only ever at size 4, 8, or 16 (never 32, see the class remarks for why).
    /// Output is in the same domain <see cref="Av1Dequantizer.Dequantize"/> produces (i.e. what
    /// <see cref="Av1InverseTransform.Inverse2D"/> expects as its <c>dequant</c> input) --
    /// <c>Av1ForwardQuantizer</c> is responsible for the forward quantization step down to entropy-codable
    /// levels.
    /// </summary>
    public static void Forward2D(ReadOnlySpan<int> residual, Span<int> coeffOut, int size, int txType = Av1TxType.DctDct)
    {
        int txSz = SizeToTxSz(size);
        double[,] rowInverse = SelectRowOperator(txSz, txType);
        double[,] colInverse = SelectColOperator(txSz, txType);

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

    /// <summary>
    /// Picks the row-pass (horizontal, i.e. the pass <see cref="Av1InverseTransform.Inverse2D"/> applies
    /// across each row's <c>w</c> values) forward operator for <paramref name="txType"/> -- IDTX's row pass
    /// is identity; DCT_DCT's and AdstDct's row pass is DCT (mirroring <c>Inverse2D</c>'s own
    /// <c>planeTxType is DctDct or AdstDct or ...</c> row-pass classification); DctAdst's and AdstAdst's is
    /// ADST.
    /// </summary>
    private static double[,] SelectRowOperator(int txSz, int txType)
    {
        if (txType == Av1TxType.Idtx)
        {
            return RowIdentityMatrices[txSz] ?? throw new ArgumentOutOfRangeException(nameof(txSz), txSz, "Av1ForwardTransform only supports IDTX at sizes 4/8/16 (TX_32X32 never reads a tx_type symbol at all -- see the class remarks).");
        }

        bool rowAdst = txType is Av1TxType.DctAdst or Av1TxType.AdstAdst;
        if (!rowAdst)
        {
            return RowInverseMatrices[txSz];
        }

        return RowAdstMatrices[txSz] ?? throw new ArgumentOutOfRangeException(nameof(txSz), txSz, "Av1ForwardTransform only supports ADST row/column passes at sizes 4/8/16 (AV1 has no ADST32).");
    }

    /// <summary>Column-pass (vertical) counterpart to <see cref="SelectRowOperator"/> -- IDTX's column pass is identity; AdstDct's and AdstAdst's column pass is ADST, DCT_DCT's and DctAdst's is DCT.</summary>
    private static double[,] SelectColOperator(int txSz, int txType)
    {
        if (txType == Av1TxType.Idtx)
        {
            return ColIdentityMatrices[txSz] ?? throw new ArgumentOutOfRangeException(nameof(txSz), txSz, "Av1ForwardTransform only supports IDTX at sizes 4/8/16 (TX_32X32 never reads a tx_type symbol at all -- see the class remarks).");
        }

        bool colAdst = txType is Av1TxType.AdstDct or Av1TxType.AdstAdst;
        if (!colAdst)
        {
            return ColInverseMatrices[txSz];
        }

        return ColAdstMatrices[txSz] ?? throw new ArgumentOutOfRangeException(nameof(txSz), txSz, "Av1ForwardTransform only supports ADST row/column passes at sizes 4/8/16 (AV1 has no ADST32).");
    }

    /// <summary>
    /// Builds, for each size in <paramref name="sizes"/>, the numeric inverse of the row (or column) operator
    /// <paramref name="inverseFn"/> applies (one of <see cref="Av1InverseTransform.InverseDct"/>/
    /// <see cref="Av1InverseTransform.InverseAdst"/>/<see cref="Av1InverseTransform.InverseIdentity"/>, each
    /// already curried down to this method's <c>(int[] t, int log2Size)</c> shape by its caller -- InverseDct/
    /// InverseAdst additionally need <see cref="ClampRange"/>, which their caller-side lambdas close over)
    /// -- indexed by <see cref="Av1TxSize"/> constant, <see langword="null"/> at any index not present in
    /// <paramref name="sizes"/> (e.g. every non-DCT array's <see cref="Av1TxSize.Tx32x32"/> slot).
    /// </summary>
    private static double[][,] BuildOperators(bool useRowShift, int[] sizes, Action<int[], int> inverseFn)
    {
        var operators = new double[4][,];
        foreach (int size in sizes)
        {
            int txSz = SizeToTxSz(size);
            int log2Size = Av1TxDimensions.WidthLog2[txSz]; // Width == Height for all square sizes.
            int shift = useRowShift ? RowShiftBySize[txSz] : ColShift;

            double[,] forwardOperatorMatrix = BuildImpulseResponseMatrix(log2Size, size, shift, inverseFn);
            operators[txSz] = Invert(forwardOperatorMatrix, size);
        }

        return operators;
    }

    /// <summary>Probes <paramref name="inverseFn"/> with unit impulses to build the matrix of the operator <c>v =&gt; Round2(inverseFn(v), shift)</c> as a linear approximation (impulse superposition ignores the small rounding nonlinearity within the probed inverse itself, which is negligible at <see cref="ImpulseAmplitude"/>'s scale).</summary>
    private static double[,] BuildImpulseResponseMatrix(int log2Size, int size, int shift, Action<int[], int> inverseFn)
    {
        var matrix = new double[size, size];
        var probe = new int[size];

        for (int k = 0; k < size; k++)
        {
            Array.Clear(probe);
            probe[k] = (int)ImpulseAmplitude;
            inverseFn(probe, log2Size);

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
