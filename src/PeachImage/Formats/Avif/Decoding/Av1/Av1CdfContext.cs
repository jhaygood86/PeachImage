namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// The live, per-tile mutable copy of the CDF tables needed for intra-only decode, reset from
/// <see cref="Av1CdfTables"/>'s defaults at the start of every tile (spec §8.2.2's "Tile*Cdf" copies).
/// Because this decoder is intra-only (<c>PrimaryRefFrame</c> is always "none" for a keyframe), AV1's
/// CDF save/load-across-frames machinery (spec §7.20) does not apply -- there is no cross-frame CDF
/// persistence to model, only the per-tile reset-from-default this type performs.
/// </summary>
internal sealed class Av1CdfContext
{
    public Av1CdfContext(int baseQIdx)
    {
        // init_coeff_cdfs() (spec §6.8.21 semantics): coefficient CDFs are seeded from one of 4
        // quantizer-indexed default slices, chosen once per frame from base_q_idx (not the per-block,
        // delta-adjusted CurrentQIndex).
        int idx = baseQIdx <= 20 ? 0 : baseQIdx <= 60 ? 1 : baseQIdx <= 120 ? 2 : 3;

        TxbSkip = Clone(Av1CdfTables.DefaultTxbSkip[idx]);
        EobPt16 = Clone(Av1CdfTables.DefaultEobPt16[idx]);
        EobPt32 = Clone(Av1CdfTables.DefaultEobPt32[idx]);
        EobPt64 = Clone(Av1CdfTables.DefaultEobPt64[idx]);
        EobPt128 = Clone(Av1CdfTables.DefaultEobPt128[idx]);
        EobPt256 = Clone(Av1CdfTables.DefaultEobPt256[idx]);
        EobPt512 = Clone(Av1CdfTables.DefaultEobPt512[idx]);
        EobPt1024 = Clone(Av1CdfTables.DefaultEobPt1024[idx]);
        EobExtra = Clone(Av1CdfTables.DefaultEobExtra[idx]);
        DcSign = Clone(Av1CdfTables.DefaultDcSign[idx]);
        CoeffBaseEob = Clone(Av1CdfTables.DefaultCoeffBaseEob[idx]);
        CoeffBase = Clone(Av1CdfTables.DefaultCoeffBase[idx]);
        CoeffBr = Clone(Av1CdfTables.DefaultCoeffBr[idx]);
    }

    public readonly ushort[][][] TxbSkip;
    public readonly ushort[][][] EobPt16;
    public readonly ushort[][][] EobPt32;
    public readonly ushort[][][] EobPt64;
    public readonly ushort[][][] EobPt128;
    public readonly ushort[][][] EobPt256;
    public readonly ushort[][] EobPt512;
    public readonly ushort[][] EobPt1024;
    public readonly ushort[][][][] EobExtra;
    public readonly ushort[][][] DcSign;
    public readonly ushort[][][][] CoeffBaseEob;
    public readonly ushort[][][][] CoeffBase;
    public readonly ushort[][][][] CoeffBr;

    public readonly ushort[][][] IntraFrameYMode = Clone(Av1CdfTables.DefaultIntraFrameYMode);
    public readonly ushort[][] UvModeCflNotAllowed = Clone(Av1CdfTables.DefaultUvModeCflNotAllowed);
    public readonly ushort[][] UvModeCflAllowed = Clone(Av1CdfTables.DefaultUvModeCflAllowed);
    public readonly ushort[][] AngleDelta = Clone(Av1CdfTables.DefaultAngleDelta);
    public readonly ushort[][] PartitionW8 = Clone(Av1CdfTables.DefaultPartitionW8);
    public readonly ushort[][] PartitionW16 = Clone(Av1CdfTables.DefaultPartitionW16);
    public readonly ushort[][] PartitionW32 = Clone(Av1CdfTables.DefaultPartitionW32);
    public readonly ushort[][] PartitionW64 = Clone(Av1CdfTables.DefaultPartitionW64);
    public readonly ushort[][] PartitionW128 = Clone(Av1CdfTables.DefaultPartitionW128);
    public readonly ushort[][] SegmentId = Clone(Av1CdfTables.DefaultSegmentId);
    public readonly ushort[][] Tx8x8 = Clone(Av1CdfTables.DefaultTx8x8);
    public readonly ushort[][] Tx16x16 = Clone(Av1CdfTables.DefaultTx16x16);
    public readonly ushort[][] Tx32x32 = Clone(Av1CdfTables.DefaultTx32x32);
    public readonly ushort[][] Tx64x64 = Clone(Av1CdfTables.DefaultTx64x64);
    public readonly ushort[] FilterIntraMode = Clone(Av1CdfTables.DefaultFilterIntraMode);
    public readonly ushort[][] FilterIntra = Clone(Av1CdfTables.DefaultFilterIntra);
    public readonly ushort[][] Skip = Clone(Av1CdfTables.DefaultSkip);
    public readonly ushort[] DeltaQ = Clone(Av1CdfTables.DefaultDeltaQ);
    public readonly ushort[] DeltaLf = Clone(Av1CdfTables.DefaultDeltaLf);
    public readonly ushort[][] DeltaLfMulti = [Clone(Av1CdfTables.DefaultDeltaLf), Clone(Av1CdfTables.DefaultDeltaLf), Clone(Av1CdfTables.DefaultDeltaLf), Clone(Av1CdfTables.DefaultDeltaLf)];
    public readonly ushort[] CflSign = Clone(Av1CdfTables.DefaultCflSign);
    public readonly ushort[][] CflAlpha = Clone(Av1CdfTables.DefaultCflAlpha);
    public readonly ushort[][][] IntraTxTypeSet1 = Clone(Av1CdfTables.DefaultIntraTxTypeSet1);
    public readonly ushort[][][] IntraTxTypeSet2 = Clone(Av1CdfTables.DefaultIntraTxTypeSet2);
    public readonly ushort[] UseWiener = Clone(Av1CdfTables.DefaultUseWiener);
    public readonly ushort[] UseSgrproj = Clone(Av1CdfTables.DefaultUseSgrproj);
    public readonly ushort[] RestorationType = Clone(Av1CdfTables.DefaultRestorationType);

    private static ushort[] Clone(ushort[] source) => (ushort[])source.Clone();

    private static ushort[][] Clone(ushort[][] source)
    {
        var result = new ushort[source.Length][];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = Clone(source[i]);
        }

        return result;
    }

    private static ushort[][][] Clone(ushort[][][] source)
    {
        var result = new ushort[source.Length][][];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = Clone(source[i]);
        }

        return result;
    }

    private static ushort[][][][] Clone(ushort[][][][] source)
    {
        var result = new ushort[source.Length][][][];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = Clone(source[i]);
        }

        return result;
    }
}
