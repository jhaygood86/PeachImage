namespace PeachImage.Formats.Avif.Decoding.Av1.IntraPrediction;

/// <summary>
/// Real basic (PAETH_PRED) intra predictor (spec §7.11.2.2), matching libaom's own
/// <c>aom_paeth_predictor_*</c> (<c>aom_dsp/x86/intrapred_{ssse3,avx2}.c</c>). See
/// <see cref="ScalarAv1PaethKernel"/> for the reference implementation every tier must match bit-exactly.
/// This is shared decode/encode code -- a bug in any tier here can desync real bitstream decoding, not just
/// degrade an encoder-only search, so every tier is held to the same bit-exact bar as the reference.
/// </summary>
internal interface IAv1PaethKernel
{
    /// <summary>
    /// Fills <paramref name="pred"/> (row-major, <paramref name="w"/> x <paramref name="h"/>) with the
    /// per-pixel Paeth choice among <paramref name="leftCol"/>[row], <paramref name="aboveRow"/>[col], and
    /// <paramref name="aboveMinus1"/> (the spec's <c>AboveRow[-1]</c> corner sample).
    /// </summary>
    void Apply(Span<int> pred, int w, int h, ReadOnlySpan<int> aboveRow, ReadOnlySpan<int> leftCol, int aboveMinus1);
}
