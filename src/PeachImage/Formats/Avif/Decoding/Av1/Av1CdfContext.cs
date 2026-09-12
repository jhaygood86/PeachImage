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
    public readonly ushort[][] InterTxTypeSet1 = Clone(Av1CdfTables.DefaultInterTxTypeSet1);
    public readonly ushort[][] InterTxTypeSet3 = Clone(Av1CdfTables.DefaultInterTxTypeSet3);
    public readonly ushort[] UseWiener = Clone(Av1CdfTables.DefaultUseWiener);
    public readonly ushort[] UseSgrproj = Clone(Av1CdfTables.DefaultUseSgrproj);
    public readonly ushort[] RestorationType = Clone(Av1CdfTables.DefaultRestorationType);

    public readonly ushort[][] PaletteYSize = Clone(Av1CdfTables.DefaultPaletteYSize);
    public readonly ushort[][] PaletteUvSize = Clone(Av1CdfTables.DefaultPaletteUvSize);
    public readonly ushort[][][] PaletteYMode = Clone(Av1CdfTables.DefaultPaletteYMode);
    public readonly ushort[][] PaletteUvMode = Clone(Av1CdfTables.DefaultPaletteUvMode);
    public readonly ushort[][][] PaletteYColorIndex = Clone(Av1CdfTables.DefaultPaletteYColorIndex);
    public readonly ushort[][][] PaletteUvColorIndex = Clone(Av1CdfTables.DefaultPaletteUvColorIndex);

    // MV/IntraBC CDFs. As explained on the Av1CdfTables default tables these come from, the spec's
    // MV_CONTEXTS context dimension is omitted (always MV_INTRABC_CONTEXT here); the comp dimension (0=row,
    // 1=col) is kept since read_mv_component(comp) adapts each independently even where the two start from
    // identical default values. mv_class0_fr/mv_class0_hp/mv_fr/mv_hp have no CDFs here at all: reduced
    // still_picture_header forces force_integer_mv=1 unconditionally for FrameIsIntra (spec order: the
    // override happens after seq_force_integer_mv's own bit is read, so this isn't a bitstream choice),
    // and read_mv_component's own syntax never reads those symbols when force_integer_mv is set -- so
    // unlike MvClass/MvClass0Bit/MvBit/MvSign, which the DV magnitude/sign always need, these four would be
    // allocated, cloned, and adapted for a code path that can provably never run. Mirrors the
    // IsAboveInter/IsLeftInter=false simplification elsewhere in this decoder.
    public readonly ushort[] Intrabc = Clone(Av1CdfTables.DefaultIntrabc);
    public readonly ushort[] MvJoint = Clone(Av1CdfTables.DefaultMvJoint);
    public readonly ushort[][] MvClass = Clone(Av1CdfTables.DefaultMvClass);
    public readonly ushort[][] MvClass0Bit = [Clone(Av1CdfTables.DefaultMvClass0Bit), Clone(Av1CdfTables.DefaultMvClass0Bit)];
    public readonly ushort[][] MvSign = [Clone(Av1CdfTables.DefaultMvSign), Clone(Av1CdfTables.DefaultMvSign)];
    public readonly ushort[][][] MvBit = [Clone(Av1CdfTables.DefaultMvBit), Clone(Av1CdfTables.DefaultMvBit)];

    /// <summary>
    /// Overwrites every one of this context's tables with <paramref name="other"/>'s current values, in
    /// place -- never reallocating any array, only copying elements, so this is safe to call once per RD
    /// candidate (<c>Av1TileEncoder.TileState.ScratchCdf</c>'s own real per-candidate use, see its remarks)
    /// or once per superblock (<c>TileState.CostCdf</c>'s own real, once-per-superblock snapshot use) without
    /// allocating.
    ///
    /// <para><b>This used to copy only the coefficient-related tables</b> (<see cref="TxbSkip"/>,
    /// <see cref="EobPt16"/>/<see cref="EobPt32"/>/<see cref="EobPt64"/>/<see cref="EobPt128"/>/
    /// <see cref="EobPt256"/>/<see cref="EobPt512"/>/<see cref="EobPt1024"/>, <see cref="EobExtra"/>,
    /// <see cref="DcSign"/>, <see cref="CoeffBaseEob"/>, <see cref="CoeffBase"/>, <see cref="CoeffBr"/> --
    /// the exact set <c>Av1CoefficientWriter.WriteCoeffs</c> reads), on the claim that "nothing outside
    /// coefficient coding ever reads a <see cref="Av1CdfContext"/> used this way". That claim was false: a
    /// direct audit found <c>Av1TileEncoder</c>'s own decision-phase/mode-search cost estimators reading
    /// <c>TileState.CostCdf</c>'s <see cref="PartitionW8"/>-<see cref="PartitionW128"/>,
    /// <see cref="UvModeCflAllowed"/>/<see cref="UvModeCflNotAllowed"/>, <see cref="AngleDelta"/>,
    /// <see cref="PaletteYSize"/>/<see cref="PaletteYMode"/>/<see cref="PaletteYColorIndex"/>/
    /// <see cref="PaletteUvSize"/>/<see cref="PaletteUvMode"/>/<see cref="PaletteUvColorIndex"/>,
    /// <see cref="IntraFrameYMode"/>, <see cref="FilterIntra"/>/<see cref="FilterIntraMode"/>, and
    /// <see cref="MvJoint"/>/<see cref="MvSign"/>/<see cref="MvClass"/>/<see cref="MvClass0Bit"/>/
    /// <see cref="MvBit"/> extensively -- every one of them left permanently at this context's own
    /// construction-time (fully default, never-adapted) values for the whole encode, since nothing ever
    /// refreshed them after the very first superblock. Every real cost comparison built on top of any of
    /// these (which candidate mode/angle_delta/palette-size/mv wins) was therefore silently scored against
    /// probabilities that never reflected anything this encoder had actually already written, for the
    /// entire life of every one of these tables -- a real, long-standing quality bug (worse candidate
    /// choices than a correctly-adapted cost model would make), not merely a missed optimization. Found via
    /// a genuine round-trip pixel-correctness investigation (a `uv_mode` decision/commit mismatch traced to
    /// exactly this staleness) even though, on its own, a stale *cost estimate* cannot itself desync a
    /// decoder (whatever candidate wins still gets written through the real, correctly-adapting
    /// <c>TileState.Cdf</c>) -- fixed regardless, since the doc comment's own claim was simply wrong and the
    /// quality impact alone easily justifies it.</para>
    /// </summary>
    public void CopyFrom(Av1CdfContext other)
    {
        Copy(IntraFrameYMode, other.IntraFrameYMode);
        Copy(UvModeCflNotAllowed, other.UvModeCflNotAllowed);
        Copy(UvModeCflAllowed, other.UvModeCflAllowed);
        Copy(AngleDelta, other.AngleDelta);
        Copy(PartitionW8, other.PartitionW8);
        Copy(PartitionW16, other.PartitionW16);
        Copy(PartitionW32, other.PartitionW32);
        Copy(PartitionW64, other.PartitionW64);
        Copy(PartitionW128, other.PartitionW128);
        Copy(SegmentId, other.SegmentId);
        Copy(Tx8x8, other.Tx8x8);
        Copy(Tx16x16, other.Tx16x16);
        Copy(Tx32x32, other.Tx32x32);
        Copy(Tx64x64, other.Tx64x64);
        Copy(FilterIntraMode, other.FilterIntraMode);
        Copy(FilterIntra, other.FilterIntra);
        Copy(Skip, other.Skip);
        Copy(DeltaQ, other.DeltaQ);
        Copy(DeltaLf, other.DeltaLf);
        Copy(DeltaLfMulti, other.DeltaLfMulti);
        Copy(CflSign, other.CflSign);
        Copy(CflAlpha, other.CflAlpha);
        Copy(IntraTxTypeSet1, other.IntraTxTypeSet1);
        Copy(IntraTxTypeSet2, other.IntraTxTypeSet2);
        Copy(InterTxTypeSet1, other.InterTxTypeSet1);
        Copy(InterTxTypeSet3, other.InterTxTypeSet3);
        Copy(UseWiener, other.UseWiener);
        Copy(UseSgrproj, other.UseSgrproj);
        Copy(RestorationType, other.RestorationType);
        Copy(PaletteYSize, other.PaletteYSize);
        Copy(PaletteUvSize, other.PaletteUvSize);
        Copy(PaletteYMode, other.PaletteYMode);
        Copy(PaletteUvMode, other.PaletteUvMode);
        Copy(PaletteYColorIndex, other.PaletteYColorIndex);
        Copy(PaletteUvColorIndex, other.PaletteUvColorIndex);
        Copy(Intrabc, other.Intrabc);
        Copy(MvJoint, other.MvJoint);
        Copy(MvClass, other.MvClass);
        Copy(MvClass0Bit, other.MvClass0Bit);
        Copy(MvSign, other.MvSign);
        Copy(MvBit, other.MvBit);

        CopyCoefficientTables(other);
    }

    /// <summary>The coefficient-related tables alone (<see cref="TxbSkip"/>, <see cref="EobPt16"/>/
    /// <see cref="EobPt32"/>/<see cref="EobPt64"/>/<see cref="EobPt128"/>/<see cref="EobPt256"/>/
    /// <see cref="EobPt512"/>/<see cref="EobPt1024"/>, <see cref="EobExtra"/>, <see cref="DcSign"/>,
    /// <see cref="CoeffBaseEob"/>, <see cref="CoeffBase"/>, <see cref="CoeffBr"/>) -- <see cref="CopyFrom"/>
    /// now copies these too (folded into its own full-context copy above); kept as its own method only
    /// because <see cref="CopyFrom"/>'s own body reads more clearly listing the non-coefficient tables
    /// first, then delegating the coefficient set's own already-existing per-field copy list here.</summary>
    private void CopyCoefficientTables(Av1CdfContext other)
    {
        Copy(TxbSkip, other.TxbSkip);
        Copy(EobPt16, other.EobPt16);
        Copy(EobPt32, other.EobPt32);
        Copy(EobPt64, other.EobPt64);
        Copy(EobPt128, other.EobPt128);
        Copy(EobPt256, other.EobPt256);
        Copy(EobPt512, other.EobPt512);
        Copy(EobPt1024, other.EobPt1024);
        Copy(EobExtra, other.EobExtra);
        Copy(DcSign, other.DcSign);
        Copy(CoeffBaseEob, other.CoeffBaseEob);
        Copy(CoeffBase, other.CoeffBase);
        Copy(CoeffBr, other.CoeffBr);
    }

    private static void Copy(ushort[] dest, ushort[] source) => Array.Copy(source, dest, source.Length);

    private static void Copy(ushort[][] dest, ushort[][] source)
    {
        for (int i = 0; i < source.Length; i++)
        {
            Copy(dest[i], source[i]);
        }
    }

    private static void Copy(ushort[][][] dest, ushort[][][] source)
    {
        for (int i = 0; i < source.Length; i++)
        {
            Copy(dest[i], source[i]);
        }
    }

    private static void Copy(ushort[][][][] dest, ushort[][][][] source)
    {
        for (int i = 0; i < source.Length; i++)
        {
            Copy(dest[i], source[i]);
        }
    }

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
