namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// One committed leaf's full structural decision -- the project plan's Phase 1/Step 6 "structural
/// per-block decision-log" record. Shared between two independent sources that both populate the exact
/// same shape: <see cref="Av1TileEncoder.EncodeTile"/>'s own real-time <c>onLeafCommitted</c> hook (fired
/// once per leaf, from inside the real commit -- see that method's own remarks), and
/// <c>tools/PeachImage.LibaomParity</c>'s own decode-side extraction (built purely by decoding a real AV1
/// bitstream -- either PeachImage's own output or aomenc's -- via <see cref="Decoding.Av1.Av1FrameDecoder"/>'s
/// exposed per-mi grids, no libaom source patching needed at all: every field here except
/// <see cref="EstimatedCost"/> is literally part of what the bitstream itself encodes, so decoding aomenc's
/// own reference output already reveals its real, ground-truth decisions).
///
/// <para><see cref="EstimatedCost"/> is the one field decode alone can never recover (aomenc's own internal
/// RD-search cost estimate isn't observable without patching its C source, which this project's own harness
/// deliberately avoids) -- <see langword="null"/> whenever a record wasn't built from a live encode with the
/// hook attached (every decode-derived record, and any encode-derived record where
/// <see cref="Av1TileEncoder.TileState.PartitionDecisions"/> didn't have a memoized entry for that exact
/// position/size for some reason).</para>
/// </summary>
internal sealed class Av1BlockDecisionRecord
{
    public required int R { get; init; }

    public required int C { get; init; }

    /// <summary>Width/height in mi units -- separate fields, not one square <c>SizeMi</c>, since a real AV1 leaf can be rectangular (a Horz/Vert-split child): equal only for a square (None-decided) leaf.</summary>
    public required int WidthMi { get; init; }

    public required int HeightMi { get; init; }

    public required int YMode { get; init; }

    public required int AngleDeltaY { get; init; }

    public required int UvMode { get; init; }

    public required int AngleDeltaUv { get; init; }

    public required bool Skip { get; init; }

    public required int PaletteSizeY { get; init; }

    public required int PaletteSizeUV { get; init; }

    /// <summary>Comma-joined color values (only the first <see cref="PaletteSizeY"/> entries are meaningful), or an empty string when <see cref="PaletteSizeY"/> is 0. A plain string, not <c>int[]</c>, so two records compare with ordinary equality instead of needing a custom array comparer.</summary>
    public required string PaletteColorsY { get; init; }

    public required string PaletteColorsUV { get; init; }

    public required bool UsedIntrabc { get; init; }

    public required int MvRow { get; init; }

    public required int MvCol { get; init; }

    public required bool UseFilterIntra { get; init; }

    public required int FilterIntraMode { get; init; }

    public long? EstimatedCost { get; init; }

    /// <summary>Field-by-field diff against <paramref name="other"/>, excluding <see cref="EstimatedCost"/> (never present on both sides at once -- see this class's own remarks) and <see cref="R"/>/<see cref="C"/>/<see cref="WidthMi"/>/<see cref="HeightMi"/> (the caller already matches records by position/size before diffing their content). Returns every differing field name plus both values, not just the first -- a real divergence often touches several related fields at once (e.g. a different y_mode almost always brings a different angle_delta with it), and seeing all of them together is what makes root-causing tractable.</summary>
    public IEnumerable<string> DiffAgainst(Av1BlockDecisionRecord other)
    {
        if (YMode != other.YMode)
        {
            yield return $"YMode: this={YMode}, other={other.YMode}";
        }

        if (AngleDeltaY != other.AngleDeltaY)
        {
            yield return $"AngleDeltaY: this={AngleDeltaY}, other={other.AngleDeltaY}";
        }

        if (UvMode != other.UvMode)
        {
            yield return $"UvMode: this={UvMode}, other={other.UvMode}";
        }

        if (AngleDeltaUv != other.AngleDeltaUv)
        {
            yield return $"AngleDeltaUv: this={AngleDeltaUv}, other={other.AngleDeltaUv}";
        }

        if (Skip != other.Skip)
        {
            yield return $"Skip: this={Skip}, other={other.Skip}";
        }

        if (PaletteSizeY != other.PaletteSizeY)
        {
            yield return $"PaletteSizeY: this={PaletteSizeY}, other={other.PaletteSizeY}";
        }

        if (PaletteSizeUV != other.PaletteSizeUV)
        {
            yield return $"PaletteSizeUV: this={PaletteSizeUV}, other={other.PaletteSizeUV}";
        }

        if (PaletteColorsY != other.PaletteColorsY)
        {
            yield return $"PaletteColorsY: this=[{PaletteColorsY}], other=[{other.PaletteColorsY}]";
        }

        if (PaletteColorsUV != other.PaletteColorsUV)
        {
            yield return $"PaletteColorsUV: this=[{PaletteColorsUV}], other=[{other.PaletteColorsUV}]";
        }

        if (UsedIntrabc != other.UsedIntrabc)
        {
            yield return $"UsedIntrabc: this={UsedIntrabc}, other={other.UsedIntrabc}";
        }

        if (MvRow != other.MvRow)
        {
            yield return $"MvRow: this={MvRow}, other={other.MvRow}";
        }

        if (MvCol != other.MvCol)
        {
            yield return $"MvCol: this={MvCol}, other={other.MvCol}";
        }

        if (UseFilterIntra != other.UseFilterIntra)
        {
            yield return $"UseFilterIntra: this={UseFilterIntra}, other={other.UseFilterIntra}";
        }

        if (FilterIntraMode != other.FilterIntraMode)
        {
            yield return $"FilterIntraMode: this={FilterIntraMode}, other={other.FilterIntraMode}";
        }
    }

    public override string ToString()
    {
        string palette = PaletteSizeY > 0 ? $", paletteY={PaletteSizeY}[{PaletteColorsY}]" : string.Empty;
        string paletteUv = PaletteSizeUV > 0 ? $", paletteUV={PaletteSizeUV}[{PaletteColorsUV}]" : string.Empty;
        string intrabc = UsedIntrabc ? $", intrabc=({MvRow},{MvCol})" : string.Empty;
        string filterIntra = UseFilterIntra ? $", filterIntra={FilterIntraMode}" : string.Empty;
        string cost = EstimatedCost is { } c ? $", estCost={c}" : string.Empty;
        return $"(r={R},c={C}) size={WidthMi}x{HeightMi} yMode={YMode}/{AngleDeltaY} uvMode={UvMode}/{AngleDeltaUv} skip={Skip}{palette}{paletteUv}{intrabc}{filterIntra}{cost}";
    }
}
