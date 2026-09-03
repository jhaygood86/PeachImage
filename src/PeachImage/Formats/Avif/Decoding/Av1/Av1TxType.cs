namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>AV1 transform type constants (spec §3, <c>TX_TYPES</c> = 16), in the spec's own enum order.</summary>
internal static class Av1TxType
{
    public const int DctDct = 0;
    public const int AdstDct = 1;
    public const int DctAdst = 2;
    public const int AdstAdst = 3;
    public const int FlipadstDct = 4;
    public const int DctFlipadst = 5;
    public const int FlipadstFlipadst = 6;
    public const int AdstFlipadst = 7;
    public const int FlipadstAdst = 8;
    public const int Idtx = 9;
    public const int VDct = 10;
    public const int HDct = 11;
    public const int VAdst = 12;
    public const int HAdst = 13;
    public const int VFlipadst = 14;
    public const int HFlipadst = 15;

    public const int Count = 16;
}

/// <summary>AV1 transform-class constants (spec §3): whether a transform type's scan/context pattern is 2D, vertical-only, or horizontal-only.</summary>
internal static class Av1TxClass
{
    public const int Class2D = 0;
    public const int ClassHoriz = 1;
    public const int ClassVert = 2;

    /// <summary><c>get_tx_class(txType)</c> (spec §8.3.2, defined alongside <c>coeff_base</c>'s CDF selection).</summary>
    public static int Get(int txType) => txType switch
    {
        Av1TxType.VDct or Av1TxType.VAdst or Av1TxType.VFlipadst => ClassVert,
        Av1TxType.HDct or Av1TxType.HAdst or Av1TxType.HFlipadst => ClassHoriz,
        _ => Class2D,
    };
}

/// <summary>
/// AV1 transform-set constants (spec §3) used by <c>get_tx_set</c>/<c>transform_type</c>. <see cref="Inter1"/>
/// is the only inter set this decoder ever reaches: IntraBC (the only way <c>is_inter</c> is ever true here)
/// is restricted to lossless blocks, which always force <c>TxSize=TX_4X4</c> -- and <c>get_tx_set(TX_4X4)</c>
/// with <c>is_inter</c> true always returns <c>TX_SET_INTER_1</c> (never Inter2/Inter3, which only apply at
/// TX_16X16 and TX_32X32-and-up respectively).
/// </summary>
internal static class Av1TxSet
{
    public const int DctOnly = 0;
    public const int Intra1 = 1;
    public const int Intra2 = 2;
    public const int Inter1 = 3;

    /// <summary>The reduced_tx_set variant of <see cref="Inter1"/> at TX_4X4 (2 symbols: IDTX, DCT_DCT only) -- see <see cref="Av1TxTypeTables.TxTypeInterInvSet3"/>.</summary>
    public const int Inter3 = 4;
}

/// <summary>
/// Small lookup tables for transform-type selection and coefficient context derivation, extracted directly
/// from the AV1 specification (§5.11.40, §5.11.47-§5.11.48, §8.3.2, §9.3).
/// </summary>
internal static class Av1TxTypeTables
{
    /// <summary><c>Tx_Type_Intra_Inv_Set1</c> (spec §5.11.47): inverts the 7-symbol <c>intra_tx_type</c> CDF read for <c>TX_SET_INTRA_1</c> back to a <see cref="Av1TxType"/> value.</summary>
    public static readonly int[] TxTypeIntraInvSet1 = [Av1TxType.Idtx, Av1TxType.DctDct, Av1TxType.VDct, Av1TxType.HDct, Av1TxType.AdstAdst, Av1TxType.AdstDct, Av1TxType.DctAdst];

    /// <summary><c>Tx_Type_Intra_Inv_Set2</c> (spec §5.11.47): the 5-symbol inversion table for <c>TX_SET_INTRA_2</c>.</summary>
    public static readonly int[] TxTypeIntraInvSet2 = [Av1TxType.Idtx, Av1TxType.DctDct, Av1TxType.AdstAdst, Av1TxType.AdstDct, Av1TxType.DctAdst];

    /// <summary><c>Tx_Type_Inter_Inv_Set1</c> (spec §5.11.47): the 16-symbol inversion table for <c>TX_SET_INTER_1</c> -- the only inter transform set an IntraBC block in this decoder ever reaches (see <see cref="Av1TxSet.Inter1"/>).</summary>
    public static readonly int[] TxTypeInterInvSet1 =
        [
            Av1TxType.Idtx, Av1TxType.VDct, Av1TxType.HDct, Av1TxType.VAdst, Av1TxType.HAdst, Av1TxType.VFlipadst, Av1TxType.HFlipadst,
            Av1TxType.DctDct, Av1TxType.AdstDct, Av1TxType.DctAdst, Av1TxType.FlipadstDct, Av1TxType.DctFlipadst, Av1TxType.AdstAdst,
            Av1TxType.FlipadstFlipadst, Av1TxType.AdstFlipadst, Av1TxType.FlipadstAdst,
        ];

    /// <summary><c>Tx_Type_Inter_Inv_Set3</c> (spec §5.11.47): the 2-symbol inversion table for <c>TX_SET_INTER_3</c> (the reduced_tx_set variant of <see cref="Av1TxSet.Inter1"/> at TX_4X4).</summary>
    public static readonly int[] TxTypeInterInvSet3 = [Av1TxType.Idtx, Av1TxType.DctDct];

    /// <summary><c>Tx_Type_In_Set_Intra[TX_SET_TYPES_INTRA][TX_TYPES]</c> (spec §5.11.40): whether a given transform type is a legal member of a given intra transform set. Indexed [DCTONLY=0, INTRA_1=1, INTRA_2=2][txType].</summary>
    public static readonly bool[][] TxTypeInSetIntra =
    [
        [true, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false],
        [true, true, true, true, false, false, false, false, false, true, true, true, false, false, false, false],
        [true, true, true, true, false, false, false, false, false, true, false, false, false, false, false, false],
    ];

    /// <summary><c>Filter_Intra_Mode_To_Intra_Dir</c> (spec §8.3.2, part of <c>intra_tx_type</c>'s CDF selection).</summary>
    public static readonly int[] FilterIntraModeToIntraDir = [Av1IntraMode.DcPred, Av1IntraMode.VPred, Av1IntraMode.HPred, Av1IntraMode.D157Pred, Av1IntraMode.DcPred];

    /// <summary><c>Mode_To_Txfm</c> (spec §9.3): the default transform type implied by a chroma intra prediction mode.</summary>
    public static readonly int[] ModeToTxfm =
    [
        Av1TxType.DctDct, // DC_PRED
        Av1TxType.AdstDct, // V_PRED
        Av1TxType.DctAdst, // H_PRED
        Av1TxType.DctDct, // D45_PRED
        Av1TxType.AdstAdst, // D135_PRED
        Av1TxType.AdstDct, // D113_PRED
        Av1TxType.DctAdst, // D157_PRED
        Av1TxType.DctAdst, // D203_PRED
        Av1TxType.AdstDct, // D67_PRED
        Av1TxType.AdstAdst, // SMOOTH_PRED
        Av1TxType.AdstDct, // SMOOTH_V_PRED
        Av1TxType.DctAdst, // SMOOTH_H_PRED
        Av1TxType.AdstAdst, // PAETH_PRED
        Av1TxType.DctDct, // UV_CFL_PRED
    ];
}
