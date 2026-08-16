namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// Lookup tables and constants for coefficient decode (spec §5.11.39, §8.3.2, §9.3), extracted directly
/// from the specification text.
/// </summary>
internal static class Av1CoeffTables
{
    public const int NumBaseLevels = 2;
    public const int CoeffBaseRange = 12;
    public const int BrCdfSize = 4;
    public const int SigCoefContextsEob = 4;
    public const int SigCoefContexts2D = 26;
    public const int SigCoefContexts = 42;
    public const int SigRefDiffOffsetNum = 5;

    /// <summary><c>Tx_Size_Sqr[t]</c>: a square tx size with side length <c>Min(w, h)</c>.</summary>
    public static readonly int[] TxSizeSqr =
    [
        Av1TxSize.Tx4x4, Av1TxSize.Tx8x8, Av1TxSize.Tx16x16, Av1TxSize.Tx32x32, Av1TxSize.Tx64x64,
        Av1TxSize.Tx4x4, Av1TxSize.Tx4x4, Av1TxSize.Tx8x8, Av1TxSize.Tx8x8, Av1TxSize.Tx16x16,
        Av1TxSize.Tx16x16, Av1TxSize.Tx32x32, Av1TxSize.Tx32x32, Av1TxSize.Tx4x4, Av1TxSize.Tx4x4,
        Av1TxSize.Tx8x8, Av1TxSize.Tx8x8, Av1TxSize.Tx16x16, Av1TxSize.Tx16x16,
    ];

    /// <summary><c>Tx_Size_Sqr_Up[t]</c>: a square tx size with side length <c>Max(w, h)</c>.</summary>
    public static readonly int[] TxSizeSqrUp =
    [
        Av1TxSize.Tx4x4, Av1TxSize.Tx8x8, Av1TxSize.Tx16x16, Av1TxSize.Tx32x32, Av1TxSize.Tx64x64,
        Av1TxSize.Tx8x8, Av1TxSize.Tx8x8, Av1TxSize.Tx16x16, Av1TxSize.Tx16x16, Av1TxSize.Tx32x32,
        Av1TxSize.Tx32x32, Av1TxSize.Tx64x64, Av1TxSize.Tx64x64, Av1TxSize.Tx16x16, Av1TxSize.Tx16x16,
        Av1TxSize.Tx32x32, Av1TxSize.Tx32x32, Av1TxSize.Tx64x64, Av1TxSize.Tx64x64,
    ];

    /// <summary><c>Adjusted_Tx_Size[t]</c> (spec §9.3): clamps 64-sided transforms down to the largest size AV1 actually codes coefficients for.</summary>
    public static readonly int[] AdjustedTxSize =
    [
        Av1TxSize.Tx4x4, Av1TxSize.Tx8x8, Av1TxSize.Tx16x16, Av1TxSize.Tx32x32, Av1TxSize.Tx32x32,
        Av1TxSize.Tx4x8, Av1TxSize.Tx8x4, Av1TxSize.Tx8x16, Av1TxSize.Tx16x8, Av1TxSize.Tx16x32,
        Av1TxSize.Tx32x16, Av1TxSize.Tx32x32, Av1TxSize.Tx32x32, Av1TxSize.Tx4x16, Av1TxSize.Tx16x4,
        Av1TxSize.Tx8x32, Av1TxSize.Tx32x8, Av1TxSize.Tx16x32, Av1TxSize.Tx32x16,
    ];

    /// <summary><c>Sig_Ref_Diff_Offset[txClass][idx][2]</c> (spec §9.3): neighbor offsets sampled by <c>get_coeff_base_ctx</c>, indexed by <see cref="Av1TxClass"/>.</summary>
    public static readonly int[][][] SigRefDiffOffset =
    [
        [[0, 1], [1, 0], [1, 1], [0, 2], [2, 0]], // TX_CLASS_2D
        [[0, 1], [1, 0], [0, 2], [0, 3], [0, 4]], // TX_CLASS_HORIZ
        [[0, 1], [1, 0], [2, 0], [3, 0], [4, 0]], // TX_CLASS_VERT
    ];

    /// <summary><c>Mag_Ref_Offset_With_Tx_Class[txClass][idx][2]</c> (spec §9.3): neighbor offsets sampled by <c>coeff_br</c>'s context derivation, indexed by <see cref="Av1TxClass"/>.</summary>
    public static readonly int[][][] MagRefOffsetWithTxClass =
    [
        [[0, 1], [1, 0], [1, 1]], // TX_CLASS_2D
        [[0, 1], [1, 0], [0, 2]], // TX_CLASS_HORIZ
        [[0, 1], [1, 0], [2, 0]], // TX_CLASS_VERT
    ];

    /// <summary><c>Coeff_Base_Pos_Ctx_Offset[3]</c> (spec §9.3): used by <c>get_coeff_base_ctx</c> for horizontal-/vertical-only transform classes.</summary>
    public static readonly int[] CoeffBasePosCtxOffset = [SigCoefContexts2D, SigCoefContexts2D + 5, SigCoefContexts2D + 10];

    /// <summary>
    /// <c>Coeff_Base_Ctx_Offset[TX_SIZES_ALL][5][5]</c> (spec §9.3), indexed by <see cref="Av1TxSize"/> in
    /// the spec's own transform-size enum order.
    /// </summary>
    public static readonly int[][][] CoeffBaseCtxOffset =
    [
        // TX_4X4
        [[0, 1, 6, 6, 0], [1, 6, 6, 21, 0], [6, 6, 21, 21, 0], [6, 21, 21, 21, 0], [0, 0, 0, 0, 0]],
        // TX_8X8
        [[0, 1, 6, 6, 21], [1, 6, 6, 21, 21], [6, 6, 21, 21, 21], [6, 21, 21, 21, 21], [21, 21, 21, 21, 21]],
        // TX_16X16
        [[0, 1, 6, 6, 21], [1, 6, 6, 21, 21], [6, 6, 21, 21, 21], [6, 21, 21, 21, 21], [21, 21, 21, 21, 21]],
        // TX_32X32
        [[0, 1, 6, 6, 21], [1, 6, 6, 21, 21], [6, 6, 21, 21, 21], [6, 21, 21, 21, 21], [21, 21, 21, 21, 21]],
        // TX_64X64
        [[0, 1, 6, 6, 21], [1, 6, 6, 21, 21], [6, 6, 21, 21, 21], [6, 21, 21, 21, 21], [21, 21, 21, 21, 21]],
        // TX_4X8
        [[0, 11, 11, 11, 0], [11, 11, 11, 11, 0], [6, 6, 21, 21, 0], [6, 21, 21, 21, 0], [21, 21, 21, 21, 0]],
        // TX_8X4
        [[0, 16, 6, 6, 21], [16, 16, 6, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21], [0, 0, 0, 0, 0]],
        // TX_8X16
        [[0, 11, 11, 11, 11], [11, 11, 11, 11, 11], [6, 6, 21, 21, 21], [6, 21, 21, 21, 21], [21, 21, 21, 21, 21]],
        // TX_16X8
        [[0, 16, 6, 6, 21], [16, 16, 6, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21]],
        // TX_16X32
        [[0, 11, 11, 11, 11], [11, 11, 11, 11, 11], [6, 6, 21, 21, 21], [6, 21, 21, 21, 21], [21, 21, 21, 21, 21]],
        // TX_32X16
        [[0, 16, 6, 6, 21], [16, 16, 6, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21]],
        // TX_32X64
        [[0, 11, 11, 11, 11], [11, 11, 11, 11, 11], [6, 6, 21, 21, 21], [6, 21, 21, 21, 21], [21, 21, 21, 21, 21]],
        // TX_64X32
        [[0, 16, 6, 6, 21], [16, 16, 6, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21]],
        // TX_4X16
        [[0, 11, 11, 11, 0], [11, 11, 11, 11, 0], [6, 6, 21, 21, 0], [6, 21, 21, 21, 0], [21, 21, 21, 21, 0]],
        // TX_16X4
        [[0, 16, 6, 6, 21], [16, 16, 6, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21], [0, 0, 0, 0, 0]],
        // TX_8X32
        [[0, 11, 11, 11, 11], [11, 11, 11, 11, 11], [6, 6, 21, 21, 21], [6, 21, 21, 21, 21], [21, 21, 21, 21, 21]],
        // TX_32X8
        [[0, 16, 6, 6, 21], [16, 16, 6, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21]],
        // TX_16X64
        [[0, 11, 11, 11, 11], [11, 11, 11, 11, 11], [6, 6, 21, 21, 21], [6, 21, 21, 21, 21], [21, 21, 21, 21, 21]],
        // TX_64X16
        [[0, 16, 6, 6, 21], [16, 16, 6, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21], [16, 16, 21, 21, 21]],
    ];
}
